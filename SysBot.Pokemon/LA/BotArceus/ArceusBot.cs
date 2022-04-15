﻿using PKHeX.Core;
using PermuteMMO.Lib;
using System.Globalization;
using ResultsUtil = SysBot.Base.ResultsUtil;
using static SysBot.Base.SwitchButton;
using static SysBot.Base.SwitchStick;
using static SysBot.Pokemon.PokeDataOffsetsLA;

namespace SysBot.Pokemon
{
    public sealed class ArceusBot : PokeRoutineExecutor8LA, IEncounterBot, IArceusBot
    {
        private readonly PokeTradeHub<PA8> Hub;
        private readonly IDumper DumpSetting;
        private readonly ArceusBotSettings Settings;
        public static bool EmbedsInitialized { get; set; }
        public static readonly List<(PA8?, bool)> EmbedMons = new();
        public static CancellationTokenSource EmbedSource { get; set; } = new();
        public ICountSettings Counts => (ICountSettings)Settings;
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        public ArceusBot(PokeBotState cfg, PokeTradeHub<PA8> hub) : base(cfg)
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        {
            Hub = hub;
            Settings = Hub.Config.Arceus;
            DumpSetting = Hub.Config.Folder;
        }

        private ulong MainNsoBase;
        private ulong OverworldOffset;
        private (string, string, string) coordinates;
        private List<PA8> boxlist = new();

        private static readonly string[] ObsidianTitle =
        {
            "Rapidash","Snorlax","Luxio","Floatzel","Staravia","Parasect","Kricketune","Stantler","Bibarel","Scyther","Lopunny","Graveler","Blissey","Heracross","Magikarp","Infernape","Alakazam","Gyarados",
        };

        private static readonly string[] CrimsonTitle =
        {
            "Tangrowth","Hippowdon","Skuntank","Onix","Rhyhorn","Honchkrow","Roserade","Lickilicky","Pachirisu","Carnivine","Vespiquen","Yanmega","Ursaring","Toxicroak","Torterra","Sliggoo","Raichu","Ursaring","Whiscash",
        };

        private static readonly string[] CoronetTitle =
        {
            "Mothim","Bronzong","Carnivine","Gligar","Gabite","Luxray","Electivire","Goodra","Steelix","Clefable","Golem","Mismagius","Rhyperior","Probopass","Gliscor",
        };

        private static readonly string[] CobaltTitle =
        {
            "Walrein","Drapion","Purugly","Ambipom","Golduck","Dusknoir","Machoke","Octillery","Mantine","Tentacruel","Ninetales","Chansey","Lumineon","Gyarados","Gastrodon","Qwilfish","Empoleon","Mothim",
        };

        private static readonly string[] AlabasterTitle =
        {
            "Glalie","Abomasnow","Mamoswine","Gardevoir","Sneasel","Chimecho","Machamp","Swinub","Piloswine","Lucario","Electabuzz","Froslass","Garchomp",
        };

        public override async Task MainLoop(CancellationToken token)
        {
            Log("Identifying trainer data of the host console.");
            await IdentifyTrainer(token).ConfigureAwait(false);
            await InitializeHardware(Settings, token).ConfigureAwait(false);
            try
            {
                Log($"Starting main {GetType().Name} loop.");
                Config.IterateNextRoutine();

                // Clear out any residual stick weirdness.
                await ResetStick(token).ConfigureAwait(false);
                MainNsoBase = await SwitchConnection.GetMainNsoBaseAsync(token).ConfigureAwait(false);
                var task = Hub.Config.Arceus.BotType switch
                {
                    ArceusMode.PlayerCoordScan => PlayerCoordScan(token),
                    ArceusMode.SeedAdvancer => SeedAdvancer(token),
                    ArceusMode.TimeSeedAdvancer => TimeSeedAdvancer(token),
                    ArceusMode.StaticAlphaScan => ScanForAlphas(token),
                    ArceusMode.DistortionSpammer => DistortionSpammer(token),
                    ArceusMode.DistortionReader => DistortionReader(token),
                    ArceusMode.MassiveOutbreakHunter => MassiveOutbreakHunter(token),
                    _ => PlayerCoordScan(token),
                };
                await task.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log(e.Message);
            }

            Log($"Ending {GetType().Name} loop.");
            await HardStop().ConfigureAwait(false);
        }

        public override async Task HardStop()
        {
            await ResetStick(CancellationToken.None).ConfigureAwait(false);
            await CleanExit(Settings, CancellationToken.None).ConfigureAwait(false);
        }

        private bool IsWaiting;
        private bool IsWaitingConfirmation;
        public void Acknowledge() => IsWaiting = false;
        public void AcknowledgeConfirmation() => IsWaitingConfirmation = false;

        private async Task TimeTest(CancellationToken token)
        {
            var timeofs = await NewParsePointer(TimePtrLA, token).ConfigureAwait(false);
            var timeVal = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(timeofs, 2, token).ConfigureAwait(false), 0);
            if (timeVal <= 7)
                timeVal = 15;
            else timeVal = 00;

            byte[] timeByte = BitConverter.GetBytes(timeVal);
            await SwitchConnection.WriteBytesAbsoluteAsync(timeByte, timeofs, token).ConfigureAwait(false);
        }

        private async Task PlayerCoordScan(CancellationToken token)
        {
            var ofs = await NewParsePointer(PlayerCoordPtrLA, token).ConfigureAwait(false);
            var coord = BitConverter.ToUInt32(await SwitchConnection.ReadBytesAbsoluteAsync(ofs, 4, token).ConfigureAwait(false), 0);
            var coord2 = BitConverter.ToUInt32(await SwitchConnection.ReadBytesAbsoluteAsync(ofs + 0x4, 4, token).ConfigureAwait(false), 0);
            var coord3 = BitConverter.ToUInt32(await SwitchConnection.ReadBytesAbsoluteAsync(ofs + 0x8, 4, token).ConfigureAwait(false), 0);

            Log($"Current Player Coords - X: {coord:X8} Y: {coord2:X8} Z: {coord3:X8}");

            if (Settings.AlphaScanConditions.AutoFillCoords == ArceusAutoFill.CampZone)
            {
                Log($"Autofilling CampZone XYZ");
                Settings.SpecialConditions.CampZoneX = $"{coord:X8}";
                Settings.SpecialConditions.CampZoneY = $"{coord2:X8}";
                Settings.SpecialConditions.CampZoneZ = $"{coord3:X8}";
            }

            if (Settings.AlphaScanConditions.AutoFillCoords == ArceusAutoFill.SpawnZone)
            {
                Log($"Autofilling SpawnZone XYZ");
                Settings.SpecialConditions.SpawnZoneX = $"{coord:X8}";
                Settings.SpecialConditions.SpawnZoneY = $"{coord2:X8}";
                Settings.SpecialConditions.SpawnZoneZ = $"{coord3:X8}";
            }
        }

        private async Task Reposition(CancellationToken token)
        {
            var menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
            while (!token.IsCancellationRequested)
            {
                Log("Not in camp, repositioning and trying again.");
                await TeleportToCampZone(token);
                for (int i = 0; i < 2; i++)
                    await Click(B, 0_500, token).ConfigureAwait(false);

                await Click(A, 1_000, token).ConfigureAwait(false);

                menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
                Log($"test: {menucheck}");
                if (menucheck == 66)
                    return;

                Log("Attempting face up");
                await TeleportToCampZone(token);
                for (int i = 0; i < 2; i++)
                    await Click(B, 0_500, token).ConfigureAwait(false);
                await SetStick(LEFT, 0, 5_000, 0_500, token).ConfigureAwait(false); // reset face forward
                await ResetStick(token).ConfigureAwait(false); // reset
                for (int i = 0; i < 2; i++)
                    await Click(B, 0_500, token).ConfigureAwait(false);

                await Click(A, 1_000, token).ConfigureAwait(false);

                menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
                Log($"test: {menucheck}");
                if (menucheck == 66)
                    return;

                Log("Attempting face down");
                await TeleportToCampZone(token);
                for (int i = 0; i < 2; i++)
                    await Click(B, 0_500, token).ConfigureAwait(false);
                await SetStick(LEFT, 0, -5_000, 0_500, token).ConfigureAwait(false); // reset face forward
                await ResetStick(token).ConfigureAwait(false); // reset                
                for (int i = 0; i < 2; i++)
                    await Click(B, 0_500, token).ConfigureAwait(false);

                await Click(A, 1_000, token).ConfigureAwait(false);

                menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
                Log($"test: {menucheck}");
                if (menucheck == 66)
                    return;
                Log("Attempting face right");
                await TeleportToCampZone(token);
                for (int i = 0; i < 2; i++)
                    await Click(B, 0_500, token).ConfigureAwait(false);
                await SetStick(LEFT, 5_000, 0, 0_500, token).ConfigureAwait(false); // reset face forward
                await ResetStick(token).ConfigureAwait(false); // reset
                for (int i = 0; i < 2; i++)
                    await Click(B, 0_500, token).ConfigureAwait(false);

                await Click(A, 1_000, token).ConfigureAwait(false);

                menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
                Log($"test: {menucheck}");
                if (menucheck == 66)
                    return;
                Log("Attempting face left");
                await TeleportToCampZone(token);
                for (int i = 0; i < 2; i++)
                    await Click(B, 0_500, token).ConfigureAwait(false);
                await SetStick(LEFT, -5_000, 0, 0_500, token).ConfigureAwait(false); // reset face forward
                await ResetStick(token).ConfigureAwait(false); // reset
                for (int i = 0; i < 2; i++)
                    await Click(B, 0_500, token).ConfigureAwait(false);

                await Click(A, 1_000, token).ConfigureAwait(false);

                menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
                Log($"test: {menucheck}");
                if (menucheck == 66)
                    return;
            }
        }

        private async Task DistortionReader(CancellationToken token)
        {
            List<PA8> matchlist = new();
            List<string> loglist = new();
            Log($"Starting Distortion Scanner for {Settings.DistortionConditions.DistortionLocation}...");
            int tries = 1;
            int count = Settings.DistortionConditions.DistortionLocation switch
            {
                ArceusMap.ObsidianFieldlands => 16,
                ArceusMap.CrimsonMirelands => 25,
                ArceusMap.CobaltCoastlands or ArceusMap.CoronetHighlands => 20,
                ArceusMap.AlabasterIcelands => 24,
                _ => 0,
            };

            while (!token.IsCancellationRequested)
            {
                Log($"Scan #{tries}...");
                for (int i = 0; i < count; i++)
                {
                    int encounter_slot_sum;
                    long[] disofs;
                    switch (Settings.DistortionConditions.DistortionLocation)
                    {
                        case ArceusMap.ObsidianFieldlands: disofs = new long[] { 0x42CC4D8, 0xC0, 0x1C0, 0x990 + i * 0x8, 0x18, 0x430, 0xC0 }; encounter_slot_sum = 112; break;
                        case ArceusMap.CrimsonMirelands: disofs = new long[] { 0x42CC4D8, 0xC0, 0x1C0, 0xC70 + i * 0x8, 0x18, 0x430, 0xC0 }; encounter_slot_sum = 276; break;
                        case ArceusMap.CobaltCoastlands: disofs = new long[] { 0x42CC4D8, 0xC0, 0x1C0, 0xCC0 + i * 0x8, 0x18, 0x430, 0xC0 }; encounter_slot_sum = 163; break;
                        case ArceusMap.CoronetHighlands: disofs = new long[] { 0x42CC4D8, 0xC0, 0x1C0, 0x818 + i * 0x8, 0x18, 0x430, 0xC0 }; encounter_slot_sum = 382; break;
                        case ArceusMap.AlabasterIcelands: disofs = new long[] { 0x42CC4D8, 0xC0, 0x1C0, 0x948 + i * 0x8, 0x18, 0x430, 0xC0 }; encounter_slot_sum = 259; break;
                        default: throw new NotImplementedException("Invalid distortion location.");
                    }

                    var SpawnerOff = SwitchConnection.PointerAll(disofs, token).Result;
                    var GeneratorSeed = SwitchConnection.ReadBytesAbsoluteAsync(SpawnerOff, 8, token).Result;
                    //Log($"GroupID: {i} | Generator Seed: {BitConverter.ToString(GeneratorSeed).Replace("-", "")}");
                    var group_seed = (BitConverter.ToUInt64(GeneratorSeed, 0) - 0x82A2B175229D6A5B) & 0xFFFFFFFFFFFFFFFF;
                    if (group_seed != 0)
                    {
                        //Log($"Group Seed: {string.Format("0x{0:X}", group_seed)}");
                        if (i >= 13 && i <= 15 && Settings.DistortionConditions.DistortionLocation == ArceusMap.CrimsonMirelands)
                            encounter_slot_sum = 118;

                        var (match, shiny, logs) = ReadDistortionSeed(i, group_seed, encounter_slot_sum);
                        loglist.Add(logs);
                        if (shiny)
                        {
                            string[] monlist = Settings.SpeciesToHunt.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (monlist.Length != 0)
                            {
                                bool huntedspecies = monlist.Contains($"{(Species)match.Species}");
                                if (!huntedspecies)
                                {
                                    EmbedMons.Add((match, false));
                                    Log(logs);
                                    break;
                                }
                            }
                            matchlist.Add(match);
                        }
                    }
                    foreach (PA8 match in matchlist)
                    {
                        if (match.IsShiny)
                        {
                            if (Settings.DistortionConditions.DistortionAlphaOnly)
                            {
                                if (!match.IsAlpha)
                                {
                                    EmbedMons.Add((match, false));
                                    break;
                                }
                            }
                            Log(loglist.Last());

                            EmbedMons.Add((match, true));
                            Settings.AddCompletedShinyAlphaFound();
                            await Click(HOME, 1_000, token).ConfigureAwait(false);
                            IsWaiting = true;
                            while (IsWaiting)
                                await Task.Delay(1_000, token).ConfigureAwait(false);

                            if (!IsWaiting)
                                await Click(HOME, 1_000, token).ConfigureAwait(false);
                        }
                    }
                    matchlist.Clear();
                }
                string report = string.Join("\n", loglist);
                Log(report);
                loglist.Clear();
                tries++;

                if (Settings.OutbreakConditions.CheckDistortionFirst && Settings.BotType == ArceusMode.MassiveOutbreakHunter)
                    return;

                await CloseGame(Hub.Config, token).ConfigureAwait(false);
                await StartGame(Hub.Config, token).ConfigureAwait(false);

            }
        }

        private string GetDistortionSpeciesLocation(int id) => Settings.DistortionConditions.DistortionLocation switch
        {
            ArceusMap.ObsidianFieldlands when id is <= 4 => "Horseshoe Plains",
            ArceusMap.ObsidianFieldlands when id is > 4 and <= 8 => "Windswept Run",
            ArceusMap.ObsidianFieldlands when id is > 8 and <= 12 => "Nature's Pantry",
            ArceusMap.ObsidianFieldlands when id is > 12 and <= 16 => "Sandgem Flats",

            ArceusMap.CrimsonMirelands when id is <= 4 => "Droning Meadow",
            ArceusMap.CrimsonMirelands when id is > 4 and <= 8 => "Holm of Trials",
            ArceusMap.CrimsonMirelands when id is > 8 and <= 12 => "Unknown",
            ArceusMap.CrimsonMirelands when id is > 12 and <= 16 => "Ursa's Ring",
            ArceusMap.CrimsonMirelands when id is > 16 and <= 20 => "Prairie",
            ArceusMap.CrimsonMirelands when id is > 20 and <= 24 => "Gapejaw Bog",

            ArceusMap.CobaltCoastlands when id is <= 4 => "Ginko Landing",
            ArceusMap.CobaltCoastlands when id is > 4 and <= 8 => "Aipom Hill",
            ArceusMap.CobaltCoastlands when id is > 8 and <= 12 => "Deadwood Haunt",
            ArceusMap.CobaltCoastlands when id is > 12 and <= 16 => "Spring Path",
            ArceusMap.CobaltCoastlands when id is > 16 and <= 20 => "Windbreak Stand",

            ArceusMap.CoronetHighlands when id is <= 4 => "Sonorous Path",
            ArceusMap.CoronetHighlands when id is > 4 and <= 8 => "Ancient Quarry",
            ArceusMap.CoronetHighlands when id is > 8 and <= 12 => "Celestica Ruins",
            ArceusMap.CoronetHighlands when id is > 12 and <= 16 => "Primeval Grotto",
            ArceusMap.CoronetHighlands when id is > 16 and <= 20 => "Boulderoll Ravine",

            ArceusMap.AlabasterIcelands when id is <= 4 => "Bonechill Wastes North",
            ArceusMap.AlabasterIcelands when id is > 4 and <= 8 => "Avalugg's Legacy",
            ArceusMap.AlabasterIcelands when id is > 8 and <= 12 => "Bonechill Wastes South",
            ArceusMap.AlabasterIcelands when id is > 12 and <= 16 => "Southeast of Arena",
            ArceusMap.AlabasterIcelands when id is > 16 and <= 20 => "Heart's Crag",
            ArceusMap.AlabasterIcelands when id is > 20 and <= 24 => "Arena's Approach",
            _ => throw new NotImplementedException("Invalid location ID."),
        };

        private PA8 GetDistortionSpecies(double encslot) => Settings.DistortionConditions.DistortionLocation switch
        {
            ArceusMap.ObsidianFieldlands when encslot is < 100 => new() { Species = (int)Species.Sneasel },
            ArceusMap.ObsidianFieldlands when encslot is > 100 and < 101 => new() { Species = (int)Species.Sneasel, IsAlpha = true },
            ArceusMap.ObsidianFieldlands when encslot is > 101 and < 111 => new() { Species = (int)Species.Weavile },
            ArceusMap.ObsidianFieldlands when encslot is > 111 and < 112 => new() { Species = (int)Species.Weavile, IsAlpha = true },

            ArceusMap.CrimsonMirelands when encslot is < 100 => new() { Species = (int)Species.Porygon },
            ArceusMap.CrimsonMirelands when encslot is > 100 and < 101 => new() { Species = (int)Species.Porygon, IsAlpha = true },
            ArceusMap.CrimsonMirelands when encslot is > 101 and < 111 => new() { Species = (int)Species.Porygon2 },
            ArceusMap.CrimsonMirelands when encslot is > 111 and < 112 => new() { Species = (int)Species.Porygon2, IsAlpha = true },
            ArceusMap.CrimsonMirelands when encslot is > 112 and < 117 => new() { Species = (int)Species.PorygonZ },
            ArceusMap.CrimsonMirelands when encslot is > 117 and < 118 => new() { Species = (int)Species.PorygonZ, IsAlpha = true },
            ArceusMap.CrimsonMirelands when encslot is > 118 and < 218 => new() { Species = (int)Species.Cyndaquil },
            ArceusMap.CrimsonMirelands when encslot is > 218 and < 219 => new() { Species = (int)Species.Cyndaquil, IsAlpha = true },
            ArceusMap.CrimsonMirelands when encslot is > 219 and < 269 => new() { Species = (int)Species.Quilava },
            ArceusMap.CrimsonMirelands when encslot is > 269 and < 270 => new() { Species = (int)Species.Quilava, IsAlpha = true },
            ArceusMap.CrimsonMirelands when encslot is > 270 and < 275 => new() { Species = (int)Species.Typhlosion },
            ArceusMap.CrimsonMirelands when encslot is >= 275 => new() { Species = (int)Species.Typhlosion, IsAlpha = true },

            ArceusMap.CobaltCoastlands when encslot is < 100 => new() { Species = (int)Species.Magnemite },
            ArceusMap.CobaltCoastlands when encslot is > 100 and < 101 => new() { Species = (int)Species.Magnemite, IsAlpha = true },
            ArceusMap.CobaltCoastlands when encslot is > 101 and < 151 => new() { Species = (int)Species.Magneton },
            ArceusMap.CobaltCoastlands when encslot is > 151 and < 152 => new() { Species = (int)Species.Magneton, IsAlpha = true },
            ArceusMap.CobaltCoastlands when encslot is > 152 and < 162 => new() { Species = (int)Species.Magnezone },
            ArceusMap.CobaltCoastlands when encslot is >= 162 => new() { Species = (int)Species.Magnezone, IsAlpha = true },

            ArceusMap.CoronetHighlands when encslot is < 100 => new() { Species = (int)Species.Cranidos },
            ArceusMap.CoronetHighlands when encslot is > 100 and < 101 => new() { Species = (int)Species.Cranidos, IsAlpha = true },
            ArceusMap.CoronetHighlands when encslot is > 101 and < 111 => new() { Species = (int)Species.Rampardos },
            ArceusMap.CoronetHighlands when encslot is > 111 and < 112 => new() { Species = (int)Species.Rampardos, IsAlpha = true },
            ArceusMap.CoronetHighlands when encslot is > 112 and < 212 => new() { Species = (int)Species.Shieldon },
            ArceusMap.CoronetHighlands when encslot is > 212 and < 213 => new() { Species = (int)Species.Shieldon, IsAlpha = true },
            ArceusMap.CoronetHighlands when encslot is > 213 and < 223 => new() { Species = (int)Species.Bastiodon },
            ArceusMap.CoronetHighlands when encslot is > 223 and < 224 => new() { Species = (int)Species.Bastiodon, IsAlpha = true },
            ArceusMap.CoronetHighlands when encslot is > 224 and < 324 => new() { Species = (int)Species.Rowlet },
            ArceusMap.CoronetHighlands when encslot is > 324 and < 325 => new() { Species = (int)Species.Rowlet, IsAlpha = true },
            ArceusMap.CoronetHighlands when encslot is > 325 and < 375 => new() { Species = (int)Species.Dartrix },
            ArceusMap.CoronetHighlands when encslot is > 375 and < 376 => new() { Species = (int)Species.Dartrix, IsAlpha = true },
            ArceusMap.CoronetHighlands when encslot is > 376 and < 381 => new() { Species = (int)Species.Decidueye },
            ArceusMap.CoronetHighlands when encslot is > 381 and < 382 => new() { Species = (int)Species.Decidueye, IsAlpha = true },

            ArceusMap.AlabasterIcelands when encslot is < 100 => new() { Species = (int)Species.Scizor },
            ArceusMap.AlabasterIcelands when encslot is > 100 and < 101 => new() { Species = (int)Species.Scizor, IsAlpha = true },
            ArceusMap.AlabasterIcelands when encslot is > 101 and < 201 => new() { Species = (int)Species.Oshawott },
            ArceusMap.AlabasterIcelands when encslot is > 201 and < 202 => new() { Species = (int)Species.Oshawott, IsAlpha = true },
            ArceusMap.AlabasterIcelands when encslot is > 202 and < 252 => new() { Species = (int)Species.Dewott },
            ArceusMap.AlabasterIcelands when encslot is > 252 and < 253 => new() { Species = (int)Species.Dewott, IsAlpha = true },
            ArceusMap.AlabasterIcelands when encslot is > 253 and < 258 => new() { Species = (int)Species.Samurott },
            ArceusMap.AlabasterIcelands when encslot is >= 258 => new() { Species = (int)Species.Samurott, IsAlpha = true },
            _ => throw new NotImplementedException("Not a valid encounter slot."),
        };

        private async Task DistortionSpammer(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                Log("Whipping up a distortion...");
                // Activates distortions
                await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0x5280010A), MainNsoBase + ActivateDistortion, token).ConfigureAwait(false);
                await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0x7100052A), MainNsoBase + ActivateDistortion, token).ConfigureAwait(false);
                var delta = DateTime.Now;
                var cd = TimeSpan.FromMinutes(Settings.DistortionConditions.WaitTimeDistortion);
                Log($"Waiting {Settings.DistortionConditions.WaitTimeDistortion} minutes then starting the next one...");
                while (DateTime.Now - delta < cd && !token.IsCancellationRequested)
                {
                    await Task.Delay(5_000, token).ConfigureAwait(false);
                    Log($"Time Remaining: {cd - (DateTime.Now - delta)}");
                }

                await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0x5280010A), MainNsoBase + ActivateDistortion, token).ConfigureAwait(false);
                await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0x7100052A), MainNsoBase + ActivateDistortion, token).ConfigureAwait(false);
                await Task.Delay(5_000, token).ConfigureAwait(false);
            }
        }

        private async Task TimeSeedAdvancer(CancellationToken token)
        {
            int success = 0;
            int heal = 0;
            string[] coords = { Settings.SpecialConditions.SpawnZoneX, Settings.SpecialConditions.SpawnZoneY, Settings.SpecialConditions.SpawnZoneZ };
            for (int a = 0; a < coords.Length; a++)
            {
                if (string.IsNullOrEmpty(coords[a]))
                {
                    Log($"One of your coordinates is empty, please fill it accordingly!");
                    return;
                }
            }
            OverworldOffset = await SwitchConnection.PointerAll(Offsets.OverworldPointer, token).ConfigureAwait(false);
            // Activates invcincible trainer cheat so we don't faint from teleporting or a Pokemon attacking and infinite PP
            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD65F03C0), MainNsoBase + InvincibleTrainer1, token).ConfigureAwait(false);
            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD65F03C0), MainNsoBase + InvincibleTrainer2, token).ConfigureAwait(false);

            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD28008AA), MainNsoBase + InfPP1, token).ConfigureAwait(false);
            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD28008A9), MainNsoBase + InfPP2, token).ConfigureAwait(false);

            GetDefaultCampCoords();
            while (!token.IsCancellationRequested)
            {
                string alpha = string.Empty;
                for (int adv = 0; adv < Settings.AlphaScanConditions.Advances; adv++)
                {
                    Log($"Advancing {Settings.AlphaScanConditions.Advances - adv} times...");
                    await Click(B, 1_000, token).ConfigureAwait(false);// Random B incase of button miss
                    await Click(A, 1_000, token).ConfigureAwait(false);
                    await TimeTest(token).ConfigureAwait(false);
                    await Task.Delay(2_000, token).ConfigureAwait(false);
                    if (Settings.AlphaScanConditions.SpawnIsStaticAlpha)
                    {
                        for (int i = 0; i < 2; i++)
                        {
                            await TimeTest(token).ConfigureAwait(false);
                            await Task.Delay(2_000, token).ConfigureAwait(false);
                        }
                    }
                    await TeleportToSpawnZone(token).ConfigureAwait(false);
                    Log("Trying to enter battle!");

                    await PressAndHold(ZL, 1_000, 0, token).ConfigureAwait(false);
                    await Click(ZR, 2_000, token).ConfigureAwait(false);
                    await ResetStick(token).ConfigureAwait(false);
                    var overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                    if (overworldcheck == 1)
                    {
                        Log("Not in battle, trying again!");
                        int tries = 0;
                        while (overworldcheck == 1)
                        {
                            if (tries == 3)
                            {
                                Log("Tried 3 times, is the encounter present? Changing time to try again.");
                                await TimeTest(token).ConfigureAwait(false);
                                await TeleportToSpawnZone(token).ConfigureAwait(false);
                                adv--;
                                break;
                            }
                            await PressAndHold(ZL, 0_800, 0, token).ConfigureAwait(false);
                            await Click(ZR, 1_000, token).ConfigureAwait(false);
                            await ResetStick(token).ConfigureAwait(false); // reset
                            overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                            if (overworldcheck != 1)
                                break;

                            await SetStick(LEFT, 0, -5_000, 0_500, token).ConfigureAwait(false); // turn around check encounter
                            await ResetStick(token).ConfigureAwait(false); // reset
                            await Click(ZL, 1_000, token).ConfigureAwait(false);
                            overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                            if (overworldcheck != 1)
                                break;

                            await PressAndHold(ZL, 0_800, 0, token).ConfigureAwait(false);
                            await Click(ZR, 1_000, token).ConfigureAwait(false);
                            await ResetStick(token).ConfigureAwait(false); // reset
                            overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                            if (overworldcheck != 1)
                                break;

                            await SetStick(LEFT, 0, 5_000, 0_500, token).ConfigureAwait(false); // turn around check encounter
                            await ResetStick(token).ConfigureAwait(false); // reset
                            await Click(ZL, 1_000, token).ConfigureAwait(false);
                            tries++;
                        }
                    }
                    if (overworldcheck == 0)
                    {
                        ulong ofs = await NewParsePointer(WildPokemonPtrLA, token).ConfigureAwait(false);
                        var pk = await ReadPokemon(ofs, 0x168, token).ConfigureAwait(false);
                        if (pk.Species > 0 && pk.Species < (int)Species.MAX_COUNT)
                        {
                            success++;
                            heal++;
                            var print = Hub.Config.StopConditions.GetAlphaPrintName(pk);
                            if (pk.IsAlpha)
                                alpha = "Alpha - ";
                            if (pk.IsShiny)
                            {
                                Log($"In battle with {print}!");
                                EmbedMons.Add((pk, true));
                                Settings.AddCompletedShinyAlphaFound();

                                if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
                                    DumpPokemon(DumpSetting.DumpFolder, "advances", pk);

                                return;
                            }
                            Log($"Mashing A to knockout {alpha}{(Species)pk.Species}!");
                            while (overworldcheck != 1)
                            {
                                overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                                pk = await ReadPokemon(ofs, 0x168, token).ConfigureAwait(false);
                                for (int i = 0; i < 3; i++)
                                    await Click(A, 0_500, token).ConfigureAwait(false);

                                if (overworldcheck == 1 || pk.Species <= 0 || pk.Species >= (int)Species.MAX_COUNT)
                                    break;
                            }
                            Log($"Defeated {alpha}{(Species)pk.Species}! Returning to spawn point.");
                            alpha = string.Empty;
                        }
                    }
                    if (adv == Settings.AlphaScanConditions.Advances)
                        return;
                    if (heal == 3)
                    {
                        Log("Returning to camp to heal our party!");
                        await TeleportToCampZone(token).ConfigureAwait(false);
                        await Click(A, 0_800, token).ConfigureAwait(false);
                        var menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
                        //Log($"Menu check: {menucheck}");
                        if (menucheck == 42)
                            await Click(A, 0_800, token).ConfigureAwait(false);
                        while (menucheck == 4000 || menucheck == 0000)
                        {
                            Log("Wrong menu opened? Backing out now and trying to reposition.");
                            await Click(B, 1_500, token).ConfigureAwait(false);
                            await Reposition(token).ConfigureAwait(false);
                            await Click(B, 1_500, token).ConfigureAwait(false);
                            menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
                            if (menucheck == 4200)
                                break;
                        }
                        await Click(A, 1_000, token).ConfigureAwait(false);
                        Log("Resting for a little while!");
                        await Click(A, 12_000, token).ConfigureAwait(false);
                        await Click(A, 1_000, token).ConfigureAwait(false);
                        await TeleportToSpawnZone(token).ConfigureAwait(false);

                    }
                    if (success == 50)
                    {
                        Log("Saving in case of a game crash!");
                        await ArceusSaveGame(token).ConfigureAwait(false);
                        success = 0;
                    }
                }
            }
            Log("Advances reached! Shiny should be on the next respawn! Refer to the live map if unsure of frame!");
            await Click(HOME, 1_000, token).ConfigureAwait(false);
        }
        private async Task SeedAdvancer(CancellationToken token)
        {
            string[] coords = { Settings.SpecialConditions.CampZoneX, Settings.SpecialConditions.CampZoneY, Settings.SpecialConditions.CampZoneZ, Settings.SpecialConditions.SpawnZoneX, Settings.SpecialConditions.SpawnZoneY, Settings.SpecialConditions.SpawnZoneZ };
            for (int a = 0; a < coords.Length; a++)
            {
                if (string.IsNullOrEmpty(coords[a]))
                {
                    Log($"One of your coordinates is empty, please fill it accordingly!");
                    return;
                }
            }
            OverworldOffset = await SwitchConnection.PointerAll(Offsets.OverworldPointer, token).ConfigureAwait(false);
            // Activates invcincible trainer cheat so we don't faint from teleporting or a Pokemon attacking
            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD65F03C0), MainNsoBase + InvincibleTrainer1, token).ConfigureAwait(false);//invi
            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD65F03C0), MainNsoBase + InvincibleTrainer2, token).ConfigureAwait(false);//invi

            while (!token.IsCancellationRequested)
            {
                GetDefaultCampCoords();
                string alpha = string.Empty;
                for (int adv = 0; adv < Settings.AlphaScanConditions.Advances; adv++)
                {
                    Log($"Advancing {Settings.AlphaScanConditions.Advances - adv} times...");
                    await Click(B, 1_000, token).ConfigureAwait(false);// Random B incase of button miss
                    await Click(A, 1_000, token).ConfigureAwait(false);
                    var menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
                    Log($"test: {menucheck}");
                    if (menucheck == 66)
                        await Click(A, 0_800, token).ConfigureAwait(false);
                    while (menucheck == 64 || menucheck == 0)
                    {
                        Log("Wrong menu opened? Backing out now and trying to reposition.");
                        await Click(B, 1_500, token).ConfigureAwait(false);
                        await Reposition(token).ConfigureAwait(false);
                        await Click(B, 1_500, token).ConfigureAwait(false);
                        menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
                        if (menucheck == 66)
                            break;
                    }
                    if (adv % 2 == 0)
                        await Click(DDOWN, 0_500, token).ConfigureAwait(false);
                    if (adv % 2 != 0)
                    {
                        await Click(DUP, 0_500, token).ConfigureAwait(false);
                        await Click(DUP, 0_500, token).ConfigureAwait(false);
                    }
                    Log("Resting...");
                    await Click(A, 12_000, token).ConfigureAwait(false);
                    Log("Rested once!");
                    await Click(A, 1_000, token).ConfigureAwait(false);
                    if (Settings.AlphaScanConditions.SpawnIsStaticAlpha)
                    {
                        // Rest a second time for Alphas
                        await SetStick(LEFT, 0, 10_000, 0_500, token).ConfigureAwait(false); // reset face forward
                        for (int i = 0; i < 2; i++)
                            await Click(A, 1_000, token).ConfigureAwait(false);
                        if (adv % 2 != 0)
                            await Click(DDOWN, 0_500, token).ConfigureAwait(false);
                        if (adv % 2 == 0)
                        {
                            for (int i = 0; i < 2; i++)
                                await Click(DUP, 0_500, token).ConfigureAwait(false);
                        }
                        Log("Resting...");
                        await Click(A, 12_000, token).ConfigureAwait(false);
                        Log("Rested twice!");
                        await Click(A, 1_000, token).ConfigureAwait(false);
                        // Rest a third time for Alphas
                        await SetStick(LEFT, 0, 10_000, 0_500, token).ConfigureAwait(false); // reset face forward
                        await ResetStick(token).ConfigureAwait(false);
                        for (int i = 0; i < 2; i++)
                            await Click(A, 1_000, token).ConfigureAwait(false);
                        if (adv % 2 == 0)
                            await Click(DDOWN, 0_500, token).ConfigureAwait(false);
                        if (adv % 2 != 0)
                        {
                            await Click(DUP, 0_500, token).ConfigureAwait(false);
                            await Click(DUP, 0_500, token).ConfigureAwait(false);
                        }
                        Log("Resting...");
                        await Click(A, 12_000, token).ConfigureAwait(false);
                        Log("Rested thrice!");
                        await Click(A, 1_000, token).ConfigureAwait(false);
                        //Night spawn only
                        if (Settings.AlphaScanConditions.NightSpawn)
                        {
                            await SetStick(LEFT, 0, 10_000, 0_500, token).ConfigureAwait(false); // reset face forward
                            await ResetStick(token).ConfigureAwait(false);
                            for (int i = 0; i < 2; i++)
                                await Click(A, 1_000, token).ConfigureAwait(false);
                            if (adv % 2 != 0)
                                await Click(DDOWN, 0_500, token).ConfigureAwait(false);
                            if (adv % 2 == 0)
                            {
                                await Click(DUP, 0_500, token).ConfigureAwait(false);
                                await Click(DUP, 0_500, token).ConfigureAwait(false);
                            }
                            Log("Resting...");
                            await Click(A, 12_000, token).ConfigureAwait(false);
                            Log("Rested for the night spawn!");
                            await Click(A, 1_000, token).ConfigureAwait(false);
                        }

                    }
                    await Click(DUP, 1_000, token).ConfigureAwait(false);
                    await SetStick(LEFT, 0, 10_000, 0_500, token).ConfigureAwait(false); // reset face forward
                    await ResetStick(token).ConfigureAwait(false);
                    await TeleportToSpawnZone(token).ConfigureAwait(false);
                    for (int i = 0; i < 2; i++)
                        await Click(B, 1_000, token).ConfigureAwait(false);

                    if (Settings.AlphaScanConditions.IsSpawnInWater)
                        await Click(PLUS, 1_000, token).ConfigureAwait(false);

                    Log("Trying to enter battle!");
                    await PressAndHold(ZL, 1_000, 0, token).ConfigureAwait(false);
                    await Click(ZR, 2_000, token).ConfigureAwait(false);
                    await ResetStick(token).ConfigureAwait(false);
                    var overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                    if (overworldcheck == 1)
                    {
                        Log("Not in battle, trying again!");
                        int tries = 0;
                        while (overworldcheck == 1)
                        {
                            if (tries == 3)
                            {
                                Log("Tried 3 times, is the encounter present? Going back to camp to try again.");
                                await TeleportToCampZone(token).ConfigureAwait(false);
                                break;
                            }
                            await PressAndHold(ZL, 0_800, 0, token).ConfigureAwait(false);
                            await Click(ZR, 1_000, token).ConfigureAwait(false);
                            await ResetStick(token).ConfigureAwait(false); // reset
                            overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                            if (overworldcheck != 1)
                                break;
                            await SetStick(LEFT, 0, -5_000, 0_500, token).ConfigureAwait(false); // turn around check encounter
                            await ResetStick(token).ConfigureAwait(false); // reset
                            await Click(ZL, 1_000, token).ConfigureAwait(false);
                            overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                            if (overworldcheck != 1)
                                break;
                            await PressAndHold(ZL, 0_800, 0, token).ConfigureAwait(false);
                            await Click(ZR, 1_000, token).ConfigureAwait(false);
                            await ResetStick(token).ConfigureAwait(false); // reset
                            overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                            if (overworldcheck != 1)
                                break;
                            await SetStick(LEFT, 0, 5_000, 0_500, token).ConfigureAwait(false); // turn around check encounter
                            await ResetStick(token).ConfigureAwait(false); // reset
                            await Click(ZL, 1_000, token).ConfigureAwait(false);
                            tries++;
                        }
                    }
                    if (overworldcheck == 0)
                    {
                        ulong ofs = await NewParsePointer(WildPokemonPtrLA, token).ConfigureAwait(false);
                        var pk = await ReadPokemon(ofs, 0x168, token).ConfigureAwait(false);
                        if (pk != null)
                        {
                            var print = Hub.Config.StopConditions.GetAlphaPrintName(pk);
                            if (pk.IsAlpha)
                                alpha = "Alpha - ";
                            if (pk.IsShiny)
                            {
                                Log($"In battle with {print}!");
                                EmbedMons.Add((pk, true));
                                Settings.AddCompletedShinyAlphaFound();

                                if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
                                    DumpPokemon(DumpSetting.DumpFolder, "advances", pk);

                                return;
                            }
                            Log($"Mashing A to knockout {alpha}{(Species)pk.Species}!");
                            while (overworldcheck != 1)
                            {
                                overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                                await Click(A, 1_000, token).ConfigureAwait(false);
                            }
                            Log($"Defeated {alpha}{(Species)pk.Species}! Returning to spawn point.");
                            alpha = string.Empty;
                        }
                    }
                    Log($"Returning to camp...");
                    if (Settings.AlphaScanConditions.IsSpawnInWater)
                        await Click(PLUS, 1_000, token).ConfigureAwait(false);

                    await TeleportToCampZone(token);
                    await SetStick(LEFT, 0, 5_000, 0_500, token).ConfigureAwait(false); // reset face forward
                    await ResetStick(token).ConfigureAwait(false); // reset
                    await ArceusSaveGame(token).ConfigureAwait(false);
                }
            }
            Log("Advances reached! Shiny should be on the next respawn! Refer to the live map if unsure of frame!");
            await Click(HOME, 1_000, token).ConfigureAwait(false);
        }

        private void GetDefaultCoords()
        {
            var mode = Settings.AlphaScanConditions.ScanLocation;
            switch (mode)
            {
                case ArceusMap.ObsidianFieldlands: coordinates = ("43AC18AC", "4242DEC9", "4305C37C"); break;
                case ArceusMap.CrimsonMirelands: coordinates = ("436208ED", "425FD612", "43DBCD87"); break;
                case ArceusMap.CoronetHighlands: coordinates = ("44636F1B", "420FDC78", "446A1DE5"); break;
                case ArceusMap.CobaltCoastlands: coordinates = ("425E5D04", "4237E63D", "44207689"); break;
                case ArceusMap.AlabasterIcelands: coordinates = ("440B8982", "41F37461", "4467EAF7"); break;
            }

            Settings.SpecialConditions.SpawnZoneX = "4408C60D";
            Settings.SpecialConditions.SpawnZoneY = "4270D7B3";
            Settings.SpecialConditions.SpawnZoneZ = "43E52E0D";

            Settings.SpecialConditions.CampZoneX = coordinates.Item1;
            Settings.SpecialConditions.CampZoneY = coordinates.Item2;
            Settings.SpecialConditions.CampZoneZ = coordinates.Item3;
        }

        private void GetDefaultCampCoords()
        {
            var mode = Settings.AlphaScanConditions.ScanLocation;
            switch (mode)
            {
                case ArceusMap.ObsidianFieldlands: coordinates = ("43B7F23A", "424FF99B", "4308306A"); break;
                case ArceusMap.CrimsonMirelands: coordinates = ("43751CD5", "425E7B13", "43D92BA9"); break;
                case ArceusMap.CoronetHighlands: coordinates = ("445E8412", "4211C885", "4466973A"); break;
                case ArceusMap.CobaltCoastlands: coordinates = ("4291B987", "4234AEB4", "441BF96B"); break;
                case ArceusMap.AlabasterIcelands: coordinates = ("4404BA24", "41F91B54", "446417D3"); break;
            }

            Settings.SpecialConditions.CampZoneX = coordinates.Item1;
            Settings.SpecialConditions.CampZoneY = coordinates.Item2;
            Settings.SpecialConditions.CampZoneZ = coordinates.Item3;
        }

        private async Task ScanForAlphas(CancellationToken token)
        {
            Settings.AlphaScanConditions.StopOnMatch = false;
            int attempts = 1;
            for (int i = 0; i < 2; i++)
                await Click(B, 0_500, token).ConfigureAwait(false);
            Log("Searching for an Alpha shiny!");
            GetDefaultCoords();
            await TeleportToCampZone(token);
            while (!token.IsCancellationRequested)
            {
                Log($"Search #{attempts}");
                await SpawnerScan(token);
                if (Settings.AlphaScanConditions.StopOnMatch)
                {
                    Log($"{Hub.Config.StopConditions.MatchFoundEchoMention} a match has been found!");
                    return;
                }
                await TeleportToCampZone(token);
                await SetStick(LEFT, -30_000, 0, 1_000, token).ConfigureAwait(false); // reset face forward
                await ResetStick(token).ConfigureAwait(false); // reset
                await Click(A, 1_000, token).ConfigureAwait(false);
                await Click(A, 1_000, token).ConfigureAwait(false);
                await Click(A, 10_000, token).ConfigureAwait(false);
                await TeleportToSpawnZone(token);
                await SetStick(LEFT, 0, -30_000, 1_000, token).ConfigureAwait(false); // reset face downward
                await ResetStick(token).ConfigureAwait(false); // reset
                for (int i = 0; i < 2; i++)
                    await Click(A, 1_000, token).ConfigureAwait(false);
                await Click(A, 10_000, token).ConfigureAwait(false);
                attempts++;
            }
        }

        private async Task TeleportToCampZone(CancellationToken token)
        {
            var ofs = await NewParsePointer(PlayerCoordPtrLA, token).ConfigureAwait(false);
            uint coordX1 = uint.Parse(Settings.SpecialConditions.CampZoneX, NumberStyles.AllowHexSpecifier);
            byte[] X1 = BitConverter.GetBytes(coordX1);
            uint coordY1 = uint.Parse(Settings.SpecialConditions.CampZoneY, NumberStyles.AllowHexSpecifier);
            byte[] Y1 = BitConverter.GetBytes(coordY1);
            uint coordZ1 = uint.Parse(Settings.SpecialConditions.CampZoneZ, NumberStyles.AllowHexSpecifier);
            byte[] Z1 = BitConverter.GetBytes(coordZ1);

            X1 = X1.Concat(Y1).Concat(Z1).ToArray();
            await SwitchConnection.WriteBytesAbsoluteAsync(X1, ofs, token).ConfigureAwait(false);

            await Task.Delay(Settings.SpecialConditions.WaitMsBetweenTeleports, token).ConfigureAwait(false);
        }

        private async Task TeleportToSpawnZone(CancellationToken token)
        {
            var ofs = await NewParsePointer(PlayerCoordPtrLA, token).ConfigureAwait(false);
            uint coordX1 = uint.Parse(Settings.SpecialConditions.SpawnZoneX, NumberStyles.AllowHexSpecifier);
            byte[] X1 = BitConverter.GetBytes(coordX1);
            uint coordY1 = uint.Parse(Settings.SpecialConditions.SpawnZoneY, NumberStyles.AllowHexSpecifier);
            byte[] Y1 = BitConverter.GetBytes(coordY1);
            uint coordZ1 = uint.Parse(Settings.SpecialConditions.SpawnZoneZ, NumberStyles.AllowHexSpecifier);
            byte[] Z1 = BitConverter.GetBytes(coordZ1);

            X1 = X1.Concat(Y1).Concat(Z1).ToArray();
            await SwitchConnection.WriteBytesAbsoluteAsync(X1, ofs, token).ConfigureAwait(false);

            await Task.Delay(Settings.SpecialConditions.WaitMsBetweenTeleports, token).ConfigureAwait(false);
        }

        private async Task TeleportToMMOGroupZone(CancellationToken token)
        {
            var ofs = await NewParsePointer(PlayerCoordPtrLA, token).ConfigureAwait(false);
            uint coordX1 = uint.Parse(coordinates.Item1, NumberStyles.AllowHexSpecifier);
            byte[] X1 = BitConverter.GetBytes(coordX1);
            uint coordY1 = uint.Parse(coordinates.Item2, NumberStyles.AllowHexSpecifier);
            byte[] Y1 = BitConverter.GetBytes(coordY1);
            uint coordZ1 = uint.Parse(coordinates.Item3, NumberStyles.AllowHexSpecifier);
            byte[] Z1 = BitConverter.GetBytes(coordZ1);

            X1 = X1.Concat(Y1).Concat(Z1).ToArray();
            await SwitchConnection.WriteBytesAbsoluteAsync(X1, ofs, token).ConfigureAwait(false);

            await Task.Delay(Settings.SpecialConditions.WaitMsBetweenTeleports, token).ConfigureAwait(false);
        }

        private async Task SpawnerScan(CancellationToken token)
        {
            await Task.Delay(0_500, token).ConfigureAwait(false);
            Log($"Starting Alpha Scanner for {Settings.AlphaScanConditions.ScanLocation}...");
            int alphacount = Settings.AlphaScanConditions.ScanLocation switch
            {
                ArceusMap.ObsidianFieldlands or ArceusMap.CobaltCoastlands => 18,
                ArceusMap.CrimsonMirelands => 19,
                ArceusMap.CoronetHighlands => 15,
                ArceusMap.AlabasterIcelands => 13,
                _ => throw new NotImplementedException("Invalid scan location."),
            };

            for (int i = 0; i < alphacount; i++)
            {
                var SpawnerOffpoint = new long[] { 0x42a6ee0, 0x330, 0x70 + i * 0x440 + 0x20 };
                var SpawnerOff = await SwitchConnection.PointerAll(SpawnerOffpoint, token).ConfigureAwait(false);
                var GeneratorSeed = await SwitchConnection.ReadBytesAbsoluteAsync(SpawnerOff, 8, token).ConfigureAwait(false);
                var group_seed = (BitConverter.ToUInt64(GeneratorSeed, 0) - 0x82A2B175229D6A5B) & 0xFFFFFFFFFFFFFFFF;
                ResultsUtil.LogText($"Generator Seed: {BitConverter.ToString(GeneratorSeed).Replace("-", "")}\nGroup Seed: {string.Format("0x{0:X}", group_seed)}");
                GenerateNextShiny(i, group_seed);
            }
        }

        private (List<PA8>, List<PA8>) ReadMMOSeed(int totalspawn, ulong group_seed, int bonuscount, ulong encslot, ulong bonusencslot)
        {
            List<PA8> monlist = new();
            List<PA8> bonuslist = new();
            var groupseed = group_seed;
            var mainrng = new Xoroshiro128Plus(groupseed);
            var sum = GrabEncounterSum(encslot, bonusencslot);

            for (int i = 0; i < 4; i++)
            {
                var spawner_seed = mainrng.Next();
                var spawner_rng = new Xoroshiro128Plus(spawner_seed);
                var encounter_slot = spawner_rng.Next() / Math.Pow(2, 64) * sum.Item1;
                var fixedseed = spawner_rng.Next();
                mainrng.Next();

                var poke = GrabMMOSpecies(encounter_slot, encslot);
                var gt = PersonalTable.LA.GetFormEntry(poke.Item1.Species, poke.Item1.Form).Gender;
                var gen = GenerateFromSeed(fixedseed, (int)Settings.OutbreakConditions.MMOShinyRolls, poke.Item2, gt);
                poke.Item1.EncryptionConstant = gen.EC;
                poke.Item1.PID = gen.PID;
                int[] pkIVList = gen.IVs;
                PKX.ReorderSpeedLast(pkIVList);
                poke.Item1.IVs = pkIVList;
                poke.Item1.Nature = (int)gen.Item8;
                poke.Item1.Gender = gen.gender;

                if (gen.shiny)
                    CommonEdits.SetShiny(poke.Item1, Shiny.Always);

                monlist.Add(poke.Item1);
            }

            groupseed = mainrng.Next();
            mainrng = new Xoroshiro128Plus(groupseed);
            var respawnrng = new Xoroshiro128Plus(groupseed);
            for (int r = 0; r < totalspawn - 4; r++)
            {
                var spawner_seed = respawnrng.Next();
                respawnrng.Next();
                respawnrng = new Xoroshiro128Plus(respawnrng.Next());
                var fixed_rng = new Xoroshiro128Plus(spawner_seed);
                var encounter_slot = fixed_rng.Next() / Math.Pow(2, 64) * sum.Item1;
                var fixed_seed = fixed_rng.Next();

                var poke = GrabMMOSpecies(encounter_slot, encslot);
                var gt = PersonalTable.LA.GetFormEntry(poke.Item1.Species, poke.Item1.Form).Gender;
                var gen = GenerateFromSeed(fixed_seed, (int)Settings.OutbreakConditions.MMOShinyRolls, poke.Item2, gt);
                int[] pkIVList = gen.IVs;
                PKX.ReorderSpeedLast(pkIVList);
                poke.Item1.IVs = pkIVList;
                poke.Item1.EncryptionConstant = gen.EC;
                poke.Item1.PID = gen.PID;
                poke.Item1.Nature = (int)gen.Item8;
                poke.Item1.Gender = gen.gender;

                if (gen.shiny)
                    CommonEdits.SetShiny(poke.Item1, Shiny.Always);

                monlist.Add(poke.Item1);
            }

            //Bonus round
            if (sum.Item2 != 0)
            {
                var bonus_seed = respawnrng.Next() - 0x82A2B175229D6A5B & 0xFFFFFFFFFFFFFFFF;
                mainrng = new Xoroshiro128Plus(bonus_seed);
                for (int i = 0; i < 4; i++)
                {
                    var spawner_seed = mainrng.Next();
                    var spawner_rng = new Xoroshiro128Plus(spawner_seed);
                    var encounter_slot = spawner_rng.Next() / Math.Pow(2, 64) * sum.Item2;
                    var fixedseed = spawner_rng.Next();
                    mainrng.Next();

                    var poke = GrabMMOSpecies(encounter_slot, bonusencslot);
                    var gt = PersonalTable.LA.GetFormEntry(poke.Item1.Species, poke.Item1.Form).Gender;
                    var gen = GenerateFromSeed(fixedseed, (int)Settings.OutbreakConditions.MMOShinyRolls, poke.Item2, gt);
                    int[] pkIVList = gen.IVs;
                    PKX.ReorderSpeedLast(pkIVList);
                    poke.Item1.IVs = pkIVList;
                    poke.Item1.EncryptionConstant = gen.EC;
                    poke.Item1.PID = gen.PID;
                    poke.Item1.Nature = (int)gen.Item8;
                    poke.Item1.Gender = gen.gender;

                    if (gen.shiny)
                        CommonEdits.SetShiny(poke.Item1, Shiny.Always);

                    bonuslist.Add(poke.Item1);
                }

                bonus_seed = mainrng.Next();
                mainrng = new Xoroshiro128Plus(bonus_seed);
                var bonusrng = new Xoroshiro128Plus(bonus_seed);
                for (int r = 0; r < bonuscount - 4; r++)
                {
                    var bonusspawner_seed = bonusrng.Next();
                    bonusrng.Next();
                    bonusrng = new Xoroshiro128Plus(bonusrng.Next());
                    var fixed_rng = new Xoroshiro128Plus(bonusspawner_seed);
                    var encounter_slot = fixed_rng.Next() / Math.Pow(2, 64) * sum.Item2;
                    var fixed_seed = fixed_rng.Next();

                    var poke = GrabMMOSpecies(encounter_slot, bonusencslot);
                    var gt = PersonalTable.LA.GetFormEntry(poke.Item1.Species, poke.Item1.Form).Gender;
                    var gen = GenerateFromSeed(fixed_seed, (int)Settings.OutbreakConditions.MMOShinyRolls, poke.Item2, gt);
                    poke.Item1.EncryptionConstant = gen.EC;
                    poke.Item1.PID = gen.PID;
                    int[] pkIVList = gen.IVs;
                    PKX.ReorderSpeedLast(pkIVList);
                    poke.Item1.IVs = pkIVList;
                    poke.Item1.Nature = (int)gen.Item8;
                    poke.Item1.Gender = gen.gender;

                    if (gen.shiny)
                        CommonEdits.SetShiny(poke.Item1, Shiny.Always);

                    bonuslist.Add(poke.Item1);
                }
            }
            return (monlist, bonuslist);
        }

        private List<PA8> ReadOutbreakSeed(Species species, int totalspawn, ulong groupseed)
        {
            List<PA8> monlist = new();
            PA8 pk = new() { Species = (int)species };
            int givs = 0;
            var mainrng = new Xoroshiro128Plus(groupseed);
            for (int i = 0; i < 4; i++)
            {
                var spawner_seed = mainrng.Next();
                mainrng.Next();
                var spawner_rng = new Xoroshiro128Plus(spawner_seed);
                var slot = spawner_rng.Next() / Math.Pow(2, 64) * 101;
                var alpha = slot >= 100;
                var fixedseed = spawner_rng.Next();

                if (alpha)
                    givs = 3;
                var gt = PersonalTable.LA.GetFormEntry(pk.Species, pk.Form).Gender;
                var gen = GenerateFromSeed(fixedseed, (int)Settings.OutbreakConditions.OutbreakShinyRolls, givs, gt);
                pk.Species = (int)species;
                pk.EncryptionConstant = gen.EC;
                pk.PID = gen.PID;
                int[] pkIVList = gen.IVs;
                PKX.ReorderSpeedLast(pkIVList);
                pk.IVs = pkIVList;
                pk.Nature = (int)gen.Item8;
                pk.Gender = gen.gender;
                pk.IsAlpha = alpha;

                if (gen.shiny)
                    CommonEdits.SetShiny(pk, Shiny.Always);

                monlist.Add(pk);
                givs = 0;
            }

            groupseed = mainrng.Next();
            mainrng = new Xoroshiro128Plus(groupseed);
            var respawnrng = new Xoroshiro128Plus(groupseed);
            for (int r = 0; r < totalspawn - 4; r++)
            {
                var spawner_seed = respawnrng.Next();
                respawnrng.Next();
                respawnrng = new Xoroshiro128Plus(respawnrng.Next());
                var fixed_rng = new Xoroshiro128Plus(spawner_seed);
                var slot = fixed_rng.Next() / Math.Pow(2, 64) * 101;
                var alpha = slot >= 100;
                var fixed_seed = fixed_rng.Next();

                if (alpha)
                    givs = 3;
                var gt = PersonalTable.LA.GetFormEntry(pk.Species, pk.Form).Gender;
                var gen = GenerateFromSeed(fixed_seed, (int)Settings.OutbreakConditions.OutbreakShinyRolls, givs, gt);
                pk.Species = (int)species;
                pk.EncryptionConstant = gen.EC;
                pk.PID = gen.PID;
                int[] pkIVList = gen.IVs;
                PKX.ReorderSpeedLast(pkIVList);
                pk.IVs = pkIVList;
                pk.Nature = (int)gen.Item8;
                pk.IsAlpha = alpha;
                pk.Gender = gen.gender;

                if (gen.shiny)
                    CommonEdits.SetShiny(pk, Shiny.Always);

                monlist.Add(pk);
                givs = 0;
            }

            return monlist;
        }

        private (PA8 match, bool shiny, string log) ReadDistortionSeed(int id, ulong group_seed, int encslotsum)
        {
            string logs = string.Empty;
            var groupseed = group_seed;
            var mainrng = new Xoroshiro128Plus(groupseed);
            var generator_seed = mainrng.Next();
            var rng = new Xoroshiro128Plus(generator_seed);
            var encounter_slot = rng.Next() / Math.Pow(2, 64) * encslotsum;
            var fixedseed = rng.Next();
            var givs = 0;
            var pk = GetDistortionSpecies(encounter_slot);
            if (pk.IsAlpha)
                givs = 3;
            var gt = PersonalTable.LA.GetFormEntry(pk.Species, pk.Form).Gender;
            var gen = GenerateFromSeed(fixedseed, (int)Settings.DistortionConditions.DistortionShinyRolls, givs, gt);

            string location = GetDistortionSpeciesLocation(id);
            if (id == 0 || id == 4 || id == 8 || id == 12 || id == 16 || id == 20)
            {
                logs += $"Ignoring Common Spawner from GroupID: {id}.";
                return (pk, false, logs);
            }

            if (id >= 9 && id <= 12 && Settings.DistortionConditions.DistortionLocation == ArceusMap.CrimsonMirelands)
            {
                logs += $"Ignoring Spawner from GroupID: {id} as location currently unknown.";
                return (pk, false, logs);
            }

            pk.EncryptionConstant = gen.EC;
            pk.PID = gen.PID;
            int[] pkIVList = gen.IVs;
            PKX.ReorderSpeedLast(pkIVList);
            pk.IVs = pkIVList;
            pk.Nature = (int)gen.Item8;
            pk.Gender = gen.gender;

            if (gen.shiny)
                CommonEdits.SetShiny(pk, Shiny.Always);

            var print = Hub.Config.StopConditions.GetAlphaPrintName(pk);
            logs += $"\nGenerator Seed: {(group_seed + 0x82A2B175229D6A5B & 0xFFFFFFFFFFFFFFFF):X16}\nGroup: {id}{print}\nEncounter Slot: {encounter_slot}\nLocation: {location}\n";
            mainrng.Next();
            mainrng.Next();
            _ = new Xoroshiro128Plus(mainrng.Next());
            return (pk, gen.shiny, logs);
        }

        private ulong GenerateNextShiny(int spawnerid, ulong seed)
        {
            int hits = 0;
            ulong newseed = 0;
            var mainrng = new Xoroshiro128Plus(seed);
            int givs = 0;
            if (Settings.AlphaScanConditions.SpawnIsStaticAlpha)
                givs = 3;

            if (!Settings.AlphaScanConditions.InItSpawn)
            {
                mainrng.Next();
                mainrng.Next();
                mainrng = new Xoroshiro128Plus(mainrng.Next());
            }

            string species = Settings.AlphaScanConditions.ScanLocation switch
            {
                ArceusMap.ObsidianFieldlands => ObsidianTitle[spawnerid],
                ArceusMap.CrimsonMirelands => CrimsonTitle[spawnerid],
                ArceusMap.CoronetHighlands => CoronetTitle[spawnerid],
                ArceusMap.CobaltCoastlands => CobaltTitle[spawnerid],
                ArceusMap.AlabasterIcelands => AlabasterTitle[spawnerid],
                _ => throw new NotImplementedException("Invalid spawner ID or location."),
            };

            for (int i = 0; i < Settings.AlphaScanConditions.MaxAdvancesToSearch; i++)
            {
                var generator_seed = mainrng.Next();
                mainrng.Next();
                var rng = new Xoroshiro128Plus(generator_seed);
                rng.Next();

                var gen = GenerateFromSeed(rng.Next(), (int)Settings.AlphaScanConditions.StaticAlphaShinyRolls, givs, 0);
                if (gen.shiny)
                {
                    if (Settings.SpeciesToHunt.Length != 0 && !Settings.SpeciesToHunt.Contains(species))
                        break;

                    if (Settings.SearchForIVs.Length != 0)
                    {
                        if (Settings.SearchForIVs.SequenceEqual(gen.IVs))
                        {
                            Log($"\nAdvances: {i}\nAlpha: {species} - {gen.shinyXor} | SpawnerID: {spawnerid}\nEC: {gen.EC:X8}\nPID: {gen.PID:X8}\nIVs: {string.Join("/", gen.IVs)}\nNature: {gen.Item8}\nSeed: {gen.Item9:X16}");
                            newseed = generator_seed;
                            Settings.AlphaScanConditions.StopOnMatch = true;
                            hits++;

                            if (hits == 3)
                            {
                                Log($"First three shiny results for {species} found.");
                                break;
                            }
                        }
                    }
                    if (Settings.SearchForIVs.Length == 0)
                    {
                        Log($"\nAdvances: {i}\nAlpha: {species} - {gen.shinyXor} | SpawnerID: {spawnerid}\nEC: {gen.EC:X8}\nPID: {gen.PID:X8}\nIVs: {string.Join("/", gen.IVs)}\nNature: {gen.Item8}\nSeed: {gen.Item9:X16}");
                        newseed = generator_seed;
                        Settings.AlphaScanConditions.StopOnMatch = true;
                        hits++;

                        if (hits == 3)
                        {
                            Log($"First three shiny results for {species} found.");
                            break;
                        }
                    }
                }
                mainrng = new Xoroshiro128Plus(mainrng.Next());
                if (i == Settings.AlphaScanConditions.MaxAdvancesToSearch - 1 && !gen.shiny)
                    Log($"No results within {Settings.AlphaScanConditions.MaxAdvancesToSearch} advances for {species} | SpawnerID: {spawnerid}.");
            }

            return newseed;
        }

        private async Task PerformOutbreakScan(CancellationToken token)
        {
            List<string> speclist = new();
            var ofs = new long[] { 0x42BA6B0, 0x2B0, 0x58, 0x18, 0x20 };
            var outbreakptr = await SwitchConnection.PointerAll(ofs, token).ConfigureAwait(false);
            var info = await SwitchConnection.ReadBytesAbsoluteAsync(outbreakptr, 0x190, token).ConfigureAwait(false);
            for (int i = 0; i < 4; i++)
            {
                int count = 0;
                Species outbreakspecies = (Species)BitConverter.ToUInt16(info.Slice(0 + (i * 80), 2));
                if (outbreakspecies != Species.None)
                {
                    var outbreakseed = BitConverter.ToUInt64(info.Slice(56 + (i * 80), 8));
                    var spawncount = BitConverter.ToUInt16(info.Slice(64 + (i * 80), 2));
                    Log($"Outbreak found for: {outbreakspecies} | Total Spawn Count: {spawncount}.");
                    var monlist = ReadOutbreakSeed(outbreakspecies, spawncount, outbreakseed);
                    foreach (PA8 pk in monlist)
                    {
                        count++;
                        var print = Hub.Config.StopConditions.GetAlphaPrintName(pk);
                        var msg = $"\nOutbreak Spawn: #{count}" + print;
                        speclist.Add(msg);

                        if (pk.IsShiny && Settings.OutbreakConditions.AlphaShinyOnly && pk.IsAlpha || pk.IsShiny && !Settings.OutbreakConditions.AlphaShinyOnly)
                        {
                            await CheckEmbed(pk, "", msg, token).ConfigureAwait(false);
                        }
                    }
                }
            }
            var res = string.Join("", speclist);
            ResultsUtil.Log(res, "[OutbreakScan]");

            if (Settings.OutbreakConditions.Permute)
            {
                Log("Beginning Outbreak permutations...");
                var (specieslist, results, moreresults) = ConsolePermuter.PermuteBlockMassOutbreak(info);
                Log("Done with permutations, check the results tab! If no results, no permutations/outbreaks are present!");
                var report = string.Join("\n", results);
                ResultsUtil.Log(report, "");
                string report2 = string.Join("\n", moreresults);
                ResultsUtil.Log(report2, "");
                string[] list = Settings.SpeciesToHunt.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                bool afk = false;
                foreach (Species s in specieslist)
                {
                    if (list.Contains(s.ToString()))
                        afk = true;
                }
                if (afk)
                {
                    Settings.AddCompletedShinyAlphaFound();
                    Log($"Desired species has a permutation!\n{report2}");
                    IsWaiting = true;
                    while (IsWaiting)
                        await Task.Delay(1_000, token).ConfigureAwait(false);
                }
                IsWaiting = false;
            }
            EmbedMons.Add((null, false));
        }

        private static (int, int) GrabEncounterSum(ulong encslot, ulong bonusslot)
        {
            int encmax = 0;
            int bonusmax = 0;
            foreach (var keyValuePair in SpawnGenerator.EncounterTables)
            {
                if (keyValuePair.Key == encslot)
                {
                    foreach (var keyValue in keyValuePair.Value)
                        encmax += keyValue.Rate;
                }

                if (keyValuePair.Key == bonusslot)
                {
                    foreach (var keyValue in keyValuePair.Value)
                        bonusmax += keyValue.Rate;
                }
            }
            return (encmax, bonusmax);
        }

        private static (PA8, int) GrabMMOSpecies(double encounter_slot, ulong encslot)
        {
            var encmax = 0;
            foreach (var keyValuePair in SpawnGenerator.EncounterTables)
            {
                if (keyValuePair.Key == encslot)
                {
                    foreach (var keyValue in keyValuePair.Value)
                    {
                        encmax += keyValue.Rate;
                        if (encounter_slot < encmax)
                            return (new PA8 { Species = keyValue.Species, Form = keyValue.Form, IsAlpha = keyValue.IsAlpha }, keyValue.FlawlessIVs);
                    }
                }
            }
            return (new(), 0);
        }

        private async Task CheckEmbed(PA8 pk, string map, string spawn, CancellationToken token)
        {
            string[] list = Settings.SpeciesToHunt.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool huntedspecies = list.Contains($"{(Species)pk.Species}");
            if (string.IsNullOrEmpty(map) && Hub.Config.Arceus.OutbreakConditions.TypeOfScan == OutbreakScanType.OutbreakOnly)
            {
                ResultsUtil.Log($"Outbreak for {(Species)pk.Species} has been found! Stopping routine execution!", "");
                IsWaiting = true;
                while (IsWaiting)
                    await Task.Delay(1_000, token).ConfigureAwait(false);
            }

            bool match = false;
            if (list.Length != 0)
            {
                if (!huntedspecies || huntedspecies && Settings.OutbreakConditions.AlphaShinyOnly && !pk.IsAlpha)
                    EmbedMons.Add((pk, false));

                if (huntedspecies && Settings.OutbreakConditions.AlphaShinyOnly && pk.IsAlpha)
                {
                    EmbedMons.Add((pk, true));
                    match = true;
                }
            }

            if (list.Length == 0 && !Settings.OutbreakConditions.CheckBoxes)
            {
                if (Settings.OutbreakConditions.AlphaShinyOnly && !pk.IsAlpha)
                    EmbedMons.Add((pk, false));
                else
                {
                    EmbedMons.Add((pk, true));
                    match = true;
                }
            }

            if (Settings.OutbreakConditions.CheckBoxes && match == false)
            {
                int f = 0;
                int n = 0;
                foreach (var mon in boxlist.ToList())
                {
                    bool alpha = !mon.IsAlpha && pk.IsAlpha;
                    bool newform = mon.Form != pk.Form;
                    bool newgender = mon.Gender != pk.Gender;
                    if (alpha || alpha && newform || alpha && newgender || alpha && newform && newgender)
                    {
                        if (mon.Species == pk.Species)
                        {
                            if (f == 0)
                            {
                                Log($"Found a {(Species)pk.Species}! It's something we don't have!\nAdding it to our boxlist!");
                                EmbedMons.Add((pk, true));
                                match = true;
                                boxlist.Add(pk);
                            }
                            f++;
                        }
                    }
                    else
                    {
                        if (n == 0)
                        {
                            EmbedMons.Add((pk, false));
                            n++;
                        }
                    }
                }
            }

            ResultsUtil.Log(spawn, "");
            if (match)
            {
                IsWaiting = true;
                IsWaitingConfirmation = true;
                Settings.AddCompletedShinyAlphaFound();
                ResultsUtil.Log($"Match found! Enter{map}, type $continue or click the Continue button and I'll teleport you to the location of {(Species)pk.Species} in a Massive Mass Outbreak!", "");
                while (IsWaiting)
                {
                    await Task.Delay(1_000, token).ConfigureAwait(false);
                    if (!IsWaitingConfirmation)
                    {
                        await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD65F03C0), MainNsoBase + InvincibleTrainer1, token).ConfigureAwait(false);
                        await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD65F03C0), MainNsoBase + InvincibleTrainer2, token).ConfigureAwait(false);

                        await TeleportToMMOGroupZone(token).ConfigureAwait(false);
                        await Click(HOME, 1_000, token).ConfigureAwait(false);
                        ResultsUtil.Log($"Teleported to the location of {(Species)pk.Species}! Pressing HOME incase you weren't ready in game.", "");
                        while (IsWaiting)
                            await Task.Delay(1_000, token).ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task PerformMMOScan(CancellationToken token)
        {
            List<string> logs = new();
            List<string> mmoactive = new();
            var ofs = new long[] { 0x42BA6B0, 0x2B0, 0x58, 0x18, 0x1B0 };
            var outbreakptr = await SwitchConnection.PointerAll(ofs, token).ConfigureAwait(false);
            var info = await SwitchConnection.ReadBytesAbsoluteAsync(outbreakptr, 14720, token).ConfigureAwait(false);

            for (int mapcount = 0; mapcount < 5; mapcount++)
            {
                ResultsUtil.LogText($"Checking map #{mapcount}...");
                ofs = new long[] { 0x42BA6B0, 0x2B0, 0x58, 0x18, 0x1B0 + (mapcount * 0xB80) };
                outbreakptr = await SwitchConnection.PointerAll(ofs, token).ConfigureAwait(false);
                var active = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(outbreakptr, 2, token).ConfigureAwait(false), 0);
                bool areavalid = $"{active:X4}" is not ("0000" or "2645");
                if (!areavalid)
                    continue;

                var mapinfo = info.Slice(mapcount * 2944, 2944);

                string map = $"{active:X4}" switch
                {
                    "56B7" => " in the Cobalt Coastlands",
                    "5504" => " in the Crimson Mirelands",
                    "5351" => " in the Alabaster Icelands",
                    "519E" => " in the Coronet Highlands",
                    "5A1D" => " in the Obsidian Fieldlands",
                    _ => throw new NotImplementedException("Unknown map."),
                };

                var groupcount = 0;
                var species = (Species)BitConverter.ToUInt16(mapinfo.Slice(36 + (groupcount * 144), 2), 0);
                while (species != Species.None && species < Species.MAX_COUNT)
                {
                    var spawncount = BitConverter.ToUInt16(mapinfo.Slice(112 + (groupcount * 144), 2), 0);
                    var encslot = BitConverter.ToUInt64(mapinfo.Slice(72 + (groupcount * 144), 8), 0);
                    var bonusencslot = BitConverter.ToUInt64(mapinfo.Slice(80 + (groupcount * 144), 8), 0);
                    var bonuscount = BitConverter.ToUInt16(mapinfo.Slice(132 + (groupcount * 144), 2), 0);
                    var group_seed = BitConverter.ToUInt64(mapinfo.Slice(104 + (groupcount * 144), 8), 0);
                    var xyz = mapinfo.Slice(16 + (groupcount * 144), 12);
                    var spawncoordx = BitConverter.ToUInt32(xyz, 0);
                    var spawncoordy = BitConverter.ToUInt32(xyz, 4);
                    var spawncoordz = BitConverter.ToUInt32(xyz, 8);
                    mmoactive.Add($"\nGroup Seed: {string.Format("0x{0:X}", group_seed)}\nMassive Mass Outbreak found for: {species}{map} | Total Spawn Count: {spawncount} | Group ID: {groupcount}");
                    List<PA8> pokelist;
                    List<PA8> bonuslist;
                    (pokelist, bonuslist) = ReadMMOSeed(spawncount, group_seed, bonuscount, encslot, bonusencslot);
                    for (int p = 0; p < pokelist.Count; p++)
                    {
                        var pk = pokelist[p];
                        var print = Hub.Config.StopConditions.GetAlphaPrintName(pk);
                        var msg = $"\nRegular Spawn: #{p + 1}" + print;
                        logs.Add(msg);
                        if (pk.IsShiny)
                        {
                            Log($"{msg}\n\nAutofilling coords for {(Species)pk.Species}. Check the Results tab for more information.");
                            coordinates = ($"{spawncoordx:X8}", $"{spawncoordy:X8}", $"{spawncoordz:X8}");
                            await CheckEmbed(pk, map, msg, token).ConfigureAwait(false);
                        }
                    }

                    for (int b = 0; b < bonuslist.Count; b++)
                    {
                        var pk = bonuslist[b];
                        var print = Hub.Config.StopConditions.GetAlphaPrintName(pk);
                        var msg = $"\nBonus Spawn: #{b + 1}" + print;
                        logs.Add(msg);
                        if (pk.IsShiny)
                        {
                            Log($"{msg}\n\nAutofilling coords for {(Species)pk.Species}. Check the Results tab for more information.");
                            coordinates = ($"{spawncoordx:X8}", $"{spawncoordy:X8}", $"{spawncoordz:X8}");
                            await CheckEmbed(pk, map, msg, token).ConfigureAwait(false);
                        }
                    }
                    var report = string.Join("\n", logs);
                    ResultsUtil.Log(report, "[MMOScan]");
                    groupcount++;
                    species = (Species)BitConverter.ToUInt16(mapinfo.Slice(36 + (groupcount * 144), 2), 0);
                }
                var actives = string.Join("", mmoactive);
                Log(actives);
            }

            if (Settings.OutbreakConditions.Permute)
            {
                string res = string.Empty;
                Log("Beginning MMO permutations...");
                var (specieslist, results, moreresults) = ConsolePermuter.PermuteMassiveMassOutbreak(info);
                Log("Done with permutations, check the results tab! If no results, no permutations/outbreaks are present!");
                var report = string.Join("\n", results);
                string report2 = string.Join("\n", moreresults);
                ResultsUtil.Log(report2, "");
                ResultsUtil.Log(report, "");
                ResultsUtil.Log(res, "");
                string[] list = Settings.SpeciesToHunt.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                bool afk = false;
                if (list.Length != 0)
                {
                    foreach (Species s in specieslist)
                    {
                        if (list.Contains(s.ToString()))
                        {
                            afk = true;
                            PA8 s1 = new();
                            s1.Species = (int)s;
                            EmbedMons.Add((s1, true));
                            Settings.AddCompletedShinyAlphaFound();
                        }
                    }
                    if (afk)
                    {
                        Log($"Desired species has a permutation!\n{report}");

                        IsWaiting = true;
                        while (IsWaiting)
                            await Task.Delay(1_000, token).ConfigureAwait(false);
                    }
                }
                if (list.Length == 0 && specieslist.Count > 0)
                {
                    IsWaiting = true;
                    while (IsWaiting)
                        await Task.Delay(1_000).ConfigureAwait(false);
                }
                IsWaiting = false;
            }
        }

        private async Task MassiveOutbreakHunter(CancellationToken token)
        {
            if (Settings.OutbreakConditions.CheckBoxes)
            {
                Log("Initiating box reader...");
                boxlist = await BoxReader(token).ConfigureAwait(false);
            }
            int attempts = 0;
            while (!token.IsCancellationRequested)
            {
                if (Settings.OutbreakConditions.CheckDistortionFirst)
                {
                    Log("Checking our distortions before scanning outbreaks!");
                    await DistortionReader(token).ConfigureAwait(false);
                }
                Log($"Search #{attempts + 1}: reading map for active outbreaks...");

                await Click(Y, 1_000, token).ConfigureAwait(false);
                while (!await IsOnOverworldTitle(token).ConfigureAwait(false))
                    await Click(A, 1_000, token).ConfigureAwait(false);

                var type = Settings.OutbreakConditions.TypeOfScan;
                switch (type)
                {
                    case OutbreakScanType.Both: await PerformOutbreakScan(token).ConfigureAwait(false); await PerformMMOScan(token).ConfigureAwait(false); break;
                    case OutbreakScanType.OutbreakOnly: await PerformOutbreakScan(token).ConfigureAwait(false); break;
                    case OutbreakScanType.MMOOnly: await PerformMMOScan(token).ConfigureAwait(false); break;
                }

                Log("No match found, resetting.");
                await Click(B, 1_000, token).ConfigureAwait(false);
                await CloseGame(Hub.Config, token).ConfigureAwait(false);
                await StartGame(Hub.Config, token).ConfigureAwait(false);
                attempts++;
            }
        }

        private async Task<List<PA8>> BoxReader(CancellationToken token)
        {
            List<PA8> pklist = new();
            for (int box = 0; box < 30; box++)
            {
                for (int i = 0; i < 30; i++)
                {
                    var b1s1 = new long[] { 0x42BA6B0, 0x1F0, 0x68 + (i * 0x168) + (box * 0x4380) };
                    var BoxStart = await SwitchConnection.PointerAll(b1s1, token).ConfigureAwait(false);
                    var mon = await ReadPokemon(BoxStart, 0x168, token).ConfigureAwait(false);
                    pklist.Add(mon);
                }
                Log($"Box: {box + 1} done...");
            }
            return pklist;
        }
    }
}