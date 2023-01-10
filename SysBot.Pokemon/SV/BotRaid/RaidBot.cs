using Discord;
using PKHeX.Core;
using SysBot.Pokemon.SV;
using System.Collections.Concurrent;
using System.Text;
using static SysBot.Base.SwitchButton;

namespace SysBot.Pokemon
{
    public class RaidSV : PokeRoutineExecutor9SV, ICountBot
    {
        private readonly PokeTradeHub<PK9> Hub;
        private readonly RaidSettingsSV Settings;
        public ICountSettings Counts => Settings;

        public static CancellationTokenSource RaidSVEmbedSource { get; set; } = new();
        public static bool RaidSVEmbedsInitialized { get; set; }
        public static ConcurrentQueue<(byte[]?, EmbedBuilder)> EmbedQueue { get; set; } = new();
        private RemoteControlAccessList RaiderBanList => Settings.RaiderBanList;

        public RaidSV(PokeBotState cfg, PokeTradeHub<PK9> hub) : base(cfg)
        {
            Hub = hub;
            Settings = hub.Config.RaidSV;
        }

        private const string RaidBotVersion = "Version 0.2.0";
        private int RaidsAtStart;
        private int RaidCount;
        private int ResetCount;
        private int WinCount;
        private int LossCount;
        private readonly Dictionary<ulong, int> RaidTracker = new();
        private SAV9SV HostSAV = new();
        private DateTime StartTime = DateTime.Now;

        private ulong OverworldOffset;
        private ulong ConnectedOffset;
        private ulong TeraRaidBlockOffset;
        private readonly ulong[] TeraNIDOffsets = new ulong[3];

        public override async Task MainLoop(CancellationToken token)
        {
            if (Settings.ConfigureRolloverCorrection)
            {
                await RolloverCorrectionSV(token).ConfigureAwait(false);
                return;
            }

            if (Settings.TimeToWait is < 0 or > 180)
            {
                Log("Time to wait must be between 0 and 180 seconds.");
                return;
            }
            try
            {
                Log("Identifying trainer data of the host console.");
                HostSAV = await IdentifyTrainer(token).ConfigureAwait(false);
                await InitializeHardware(Settings, token).ConfigureAwait(false);

                Log("Starting main RaidBot loop.");
                await InnerLoop(token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log(e.Message);
            }

            Log($"Ending {nameof(RaidBot)} loop.");
            await HardStop().ConfigureAwait(false);
        }

        private async Task InnerLoop(CancellationToken token)
        {
            bool partyReady;
            List<(ulong, TradeMyStatus)> lobbyTrainers;
            StartTime = DateTime.Now;

            while (!token.IsCancellationRequested)
            {
                // Initialize offsets at the start of the routine and cache them.
                await InitializeSessionOffsets(token).ConfigureAwait(false);

                // Get initial raid counts for comparison later.
                await CountRaids(null, token).ConfigureAwait(false);

                // Clear NIDs.
                await SwitchConnection.WriteBytesAbsoluteAsync(new byte[32], TeraNIDOffsets[0], token).ConfigureAwait(false);

                // Connect online and enter den.
                if (!await PrepareForRaid(token).ConfigureAwait(false))
                {
                    Log("Failed to prepare the raid. Stopping the routine.");
                    return;
                }

                // Wait until we're in lobby.
                if (!await GetLobbyReady(token).ConfigureAwait(false))
                    continue;

                // Read trainers until someone joins.
                (partyReady, lobbyTrainers) = await ReadTrainers(token).ConfigureAwait(false);
                if (!partyReady)
                {
                    // Should add overworld recovery with a game restart fallback.
                    await RegroupFromBannedUser(token).ConfigureAwait(false);
                    continue;
                }

                await CompleteRaid(lobbyTrainers, token).ConfigureAwait(false);
            }
        }

        public override async Task HardStop()
        {
            RaidSVEmbedsInitialized = false;
            RaidSVEmbedSource.Cancel();
            await CleanExit(Settings, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task CompleteRaid(List<(ulong, TradeMyStatus)> trainers, CancellationToken token)
        {
            if (await IsConnectedToLobby(token).ConfigureAwait(false))
            {
                int b = 0;
                Log("Preparing for battle!");
                while (!await IsInRaid(token).ConfigureAwait(false))                
                    await Click(A, 1_000, token).ConfigureAwait(false);                

                while (await IsConnectedToLobby(token).ConfigureAwait(false))
                {
                    b++;
                    await Click(A, 3_000, token).ConfigureAwait(false);
                    if (b % 10 == 0)
                        Log("Still in battle...");
                }
            }

            Log("Raid lobby disbanded!");
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Click(DDOWN, 0_500, token).ConfigureAwait(false);

            Log("Returning to overworld...");
            while (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                await Click(A, 1_000, token).ConfigureAwait(false);

            await CountRaids(trainers, token).ConfigureAwait(false);

            ResetCount++;
            await CloseGame(Hub.Config, token).ConfigureAwait(false);

            if (ResetCount == Settings.RollbackTimeAfterThisManyRaids && Settings.RollbackTime)
            {
                Log("Applying rollover correction.");
                await RolloverCorrectionSV(token).ConfigureAwait(false);
                ResetCount = 0;
            }
            await StartGame(Hub.Config, token).ConfigureAwait(false);
        }

        private void ApplyPenalty(List<(ulong, TradeMyStatus)> trainers)
        {
            for (int i = 0; i < trainers.Count; i++)
            {
                var nid = trainers[i].Item1;
                var name = trainers[i].Item2.OT;
                if (RaidTracker.ContainsKey(nid) && nid != 0)
                {
                    var entry = RaidTracker[nid];
                    var penalty = entry + 1;
                    RaidTracker[nid] = penalty;
                    Log($"Player: {name} completed the raid with Penalty Count: {penalty}.");

                    if (penalty > Settings.CatchLimit && !RaiderBanList.Contains(nid) && Settings.CatchLimit != 0)
                    {
                        Log($"Player: {name} exceeded the catch limit {penalty}/{Settings.CatchLimit} for {Settings.RaidSpecies} on {DateTime.Now}.");
                        RaiderBanList.List.Add(new() { ID = nid, Name = name, Comment = $"Player: {name} exceeded the catch limit {penalty}/{Settings.CatchLimit} for {Settings.RaidSpecies} on {DateTime.Now}." });
                    }
                }
            }
        }

        private async Task CountRaids(List<(ulong, TradeMyStatus)>? trainers, CancellationToken token)
        {
            List<uint> seeds = new();
            var data = await SwitchConnection.ReadBytesAbsoluteAsync(TeraRaidBlockOffset, 2304, token).ConfigureAwait(false);

            for (int i = 0; i < 69; i++)
            {
                var seed = BitConverter.ToUInt32(data.Slice(0 + (i * 32), 4));
                if (seed != 0)
                    seeds.Add(seed);
            }

            Log($"Active raid count: {seeds.Count}");
            if (RaidCount == 0)
            {
                RaidsAtStart = seeds.Count;
                return;
            }

            if (trainers is not null)
            {
                Log("Back in the overworld, checking if we won or lost.");
                Settings.AddCompletedRaids();
                if (RaidsAtStart > seeds.Count)
                {
                    Log("We defeated the raid boss!");
                    WinCount++;
                    if (trainers.Count > 0)
                        ApplyPenalty(trainers);
                    return;
                }

                Log("We lost the raid...");
                LossCount++;
            }
        }

        private async Task<bool> PrepareForRaid(CancellationToken token)
        {
            Log("Preparing lobby...");

            // Make sure we're connected.
            while (!await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
            {
                Log("Connecting...");
                await RecoverToOverworld(token).ConfigureAwait(false);
                if (!await ConnectToOnline(Hub.Config, token).ConfigureAwait(false))
                    return false;
            }

            // Recover and check if we're connected again.
            await RecoverToOverworld(token).ConfigureAwait(false);
            if (!await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
            {
                Log("Connecting again...");
                if (!await ConnectToOnline(Hub.Config, token).ConfigureAwait(false))
                    return false;
            }

            await Click(B, 2_000, token).ConfigureAwait(false);
            await Click(A, 3_000, token).ConfigureAwait(false);
            await Click(A, 3_000, token).ConfigureAwait(false);

            if (!Settings.CodeTheRaid)
                await Click(DDOWN, 1_000, token).ConfigureAwait(false);

            await Click(A, 8_000, token).ConfigureAwait(false);
            return true;
        }

        private async Task<bool> GetLobbyReady(CancellationToken token)
        {
            var x = 0;
            Log("Connecting to lobby...");
            while (!await IsConnectedToLobby(token).ConfigureAwait(false))
            {
                await Click(A, 1_000, token).ConfigureAwait(false);
                x++;
                if (x == 30)
                {
                    Log("Failed to connect to lobby, restarting game incase we were in battle/bad connection.");
                    await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
                    Log("Attempting to restart routine!");
                    return false;
                }
            }
            return true;
        }

        private async Task<string> GetRaidCode(CancellationToken token)
        {
            var data = await SwitchConnection.PointerPeek(6, Offsets.TeraRaidCodePointer, token).ConfigureAwait(false);
            string str = Encoding.ASCII.GetString(data);
            if (!char.IsLetterOrDigit(str[0]))
                str = "Free For All";

            Log($"Raid Code: {str}");
            return $"\n{str}\n";
        }

        private async Task<bool> CheckIfTrainerBanned(TradeMyStatus trainer, ulong nid, int player, CancellationToken token)
        {
            Log($"Player {player} - {trainer.OT} | TID: {trainer.DisplayTID} | NID: {nid}");
            if (!RaidTracker.ContainsKey(nid))
                RaidTracker.Add(nid, 0);

            bool updateBanList = RaidCount == 0 || RaidCount % Settings.RaidsBetweenUpdate == 0;
            var banResultCC = await BanService.IsRaiderBanned(trainer.OT, Settings.BanListURL, Connection.Label, updateBanList).ConfigureAwait(false);
            var banResultCFW = RaiderBanList.List.FirstOrDefault(x => x.ID == nid);

            bool isBanned = banResultCC.Item1 || banResultCFW != default;
            if (isBanned)
            {
                var msg = banResultCC.Item1 ? banResultCC.Item2 : $"\nBanned user {banResultCFW!.Name} found in the host's ban list.\n{banResultCFW.Comment}";
                Log(msg);

                await EnqueueEmbed(null, msg, false, true, token).ConfigureAwait(false);
                return true;
            }

            return false;
        }

        // This is messy, needs a way to check if player X is ready, and when we're in a raid, in order to avoid adding players that may have disconnected or quit. Players get shifted down as they leave.
        private async Task<(bool, List<(ulong, TradeMyStatus)>)> ReadTrainers(CancellationToken token)
        {
            await EnqueueEmbed(null, "", false, false, token).ConfigureAwait(false);

            List<(ulong, TradeMyStatus)> lobbyTrainers = new();
            var wait = TimeSpan.FromSeconds(Settings.TimeToWait);
            var endTime = DateTime.Now + wait;
            bool full = false;

            while (!full && (DateTime.Now < endTime))
            {
                // Loop through trainers
                for (int i = 0; i < 3; i++)
                {
                    var player = i + 2;
                    Log($"Waiting for Player {player} to load...");

                    var nidOfs = TeraNIDOffsets[i];
                    var data = await SwitchConnection.ReadBytesAbsoluteAsync(nidOfs, 8, token).ConfigureAwait(false);
                    var nid = BitConverter.ToUInt64(data, 0);
                    while (nid == 0 && (DateTime.Now < endTime))
                    {
                        await Task.Delay(0_500, token).ConfigureAwait(false);
                        data = await SwitchConnection.ReadBytesAbsoluteAsync(nidOfs, 8, token).ConfigureAwait(false);
                        nid = BitConverter.ToUInt64(data, 0);
                    }

                    var info = new TradeMyStatus();
                    var pointer = new long[] { 0x437ECE0, 0x48, 0xE0 + (i * 0x30), 0x0 };
                    var trainer = await GetTradePartnerMyStatus(pointer, token).ConfigureAwait(false);

                    while (trainer.OT.Length == 0 && (DateTime.Now < endTime))
                    {
                        await Task.Delay(0_500, token).ConfigureAwait(false);
                        trainer = await GetTradePartnerMyStatus(pointer, token).ConfigureAwait(false);
                    }

                    if (lobbyTrainers.FirstOrDefault(x => x.Item1 == nid && x.Item2.OT == trainer.OT) != default)
                        lobbyTrainers[i] = (nid, trainer);
                    else if (nid > 0 && trainer.OT.Length > 0)
                        lobbyTrainers.Add((nid, trainer));

                    full = lobbyTrainers.Count == 3;
                    if (full || (DateTime.Now >= endTime))
                        break;
                }
            }

            // Loop through trainers again in case someone disconnected.
            List<(ulong, TradeMyStatus)> lobbyTrainersFinal = new();
            for (int i = 0; i < 3; i++)
            {
                var player = i + 2;
                var nidOfs = TeraNIDOffsets[i];
                var data = await SwitchConnection.ReadBytesAbsoluteAsync(nidOfs, 8, token).ConfigureAwait(false);
                var nid = BitConverter.ToUInt64(data, 0);

                var info = new TradeMyStatus();
                var pointer = new long[] { 0x437ECE0, 0x48, 0xE0 + (i * 0x30), 0x0 };
                var trainer = await GetTradePartnerMyStatus(pointer, token).ConfigureAwait(false);

                if (nid != 0 && !string.IsNullOrWhiteSpace(trainer.OT))
                {
                    if (await CheckIfTrainerBanned(trainer, nid, player, token).ConfigureAwait(false))
                        return (false, lobbyTrainersFinal);

                    lobbyTrainersFinal.Add((nid, trainer));
                }
            }

            RaidCount++;
            if (lobbyTrainersFinal.Count == 0)
            {
                Log("Nobody joined the raid, recovering...");
                return (false, lobbyTrainers);
            }
            Log($"Raid #{RaidCount} is starting!");

            var names = lobbyTrainersFinal.Select(x => x.Item2.OT).ToList();
            bool hatTrick = lobbyTrainersFinal.Count == 3 && names.Distinct().Count() == 1;

            await Task.Delay(2_000, token).ConfigureAwait(false);
            await EnqueueEmbed(names, "", hatTrick, false, token).ConfigureAwait(false);
            return (true, lobbyTrainersFinal);
        }

        private async Task<bool> IsConnectedToLobby(CancellationToken token)
        {
            var data = await SwitchConnection.ReadBytesMainAsync(Offsets.TeraLobby, 1, token).ConfigureAwait(false);
            return data[0] != 0x00; // 0 when in lobby but not connected
        }

        private async Task<bool> IsInRaid(CancellationToken token)
        {
            var data = await SwitchConnection.ReadBytesMainAsync(Offsets.LoadedIntoRaid, 1, token).ConfigureAwait(false);
            return data[0] == 0x02; // 2 when in raid, 1 when not
        }

        private async Task RolloverCorrectionSV(CancellationToken token)
        {
            // May try to just roll day back if raid is missing instead of rolling time back every X raids

            for (int i = 0; i < 2; i++)
                await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(DDOWN, 0_150, token).ConfigureAwait(false);
            await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(A, 1_250, token).ConfigureAwait(false); // Enter settings

            await PressAndHold(DDOWN, 2_000, 0_250, token).ConfigureAwait(false); // Scroll to system settings
            await Click(A, 1_250, token).ConfigureAwait(false);

            await PressAndHold(DDOWN, Settings.TimeToScrollDownForRollover, 0, token).ConfigureAwait(false);
            await Click(DUP, 0_500, token).ConfigureAwait(false);

            await Click(A, 1_250, token).ConfigureAwait(false);
            for (int i = 0; i < 2; i++)
                await Click(DDOWN, 0_150, token).ConfigureAwait(false);
            await Click(A, 0_500, token).ConfigureAwait(false);
            for (int i = 0; i < 3; i++)// try 1 for Day change instead of 3 for Hour
                await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(DDOWN, 0_150, token).ConfigureAwait(false);
            for (int i = 0; i < 4; i++)// try 6 instead of 4 if doing Day for Hour
                await Click(A, 0_200, token).ConfigureAwait(false);

            await Click(HOME, 1_000, token).ConfigureAwait(false); // Back to title screen
        }

        private async Task RegroupFromBannedUser(CancellationToken token)
        {
            await Click(B, 1_250, token).ConfigureAwait(false);
            await Click(A, 3_000, token).ConfigureAwait(false);
            await Click(A, 1_500, token).ConfigureAwait(false);
            await Click(B, 1_000, token).ConfigureAwait(false);
        }

        private async Task InitializeSessionOffsets(CancellationToken token)
        {
            Log("Caching session offsets...");
            OverworldOffset = await SwitchConnection.PointerAll(Offsets.OverworldPointer, token).ConfigureAwait(false);
            ConnectedOffset = await SwitchConnection.PointerAll(Offsets.IsConnectedPointer, token).ConfigureAwait(false);
            TeraRaidBlockOffset = await SwitchConnection.PointerAll(Offsets.TeraRaidBlockPointer, token).ConfigureAwait(false);            

            var nidPointer = new long[] { Offsets.LinkTradePartnerNIDPointer[0], Offsets.LinkTradePartnerNIDPointer[1], Offsets.LinkTradePartnerNIDPointer[2] };
            for (int p = 0; p < TeraNIDOffsets.Length; p++)
            {
                nidPointer[2] = Offsets.LinkTradePartnerNIDPointer[2] + (p * 0x8);
                TeraNIDOffsets[p] = await SwitchConnection.PointerAll(nidPointer, token).ConfigureAwait(false);
            }
            Log("Caching offsets complete!");
        }

        private async Task EnqueueEmbed(List<string>? names, string message, bool hatTrick, bool disband, CancellationToken token)
        {
            if (RaidSVEmbedsInitialized)
            {
                // Title can only be up to 256 characters.
                var title = hatTrick && names is not null ? $"**🪄🎩✨ {names[0]} with the Hat Trick! ✨🎩🪄**" : Settings.RaidEmbedTitle.Length > 0 ? Settings.RaidEmbedTitle : "Tera Raid Notification";
                if (title.Length > 256)
                    title = title[..256];

                // Description can only be up to 4096 characters.
                var description = Settings.RaidEmbedDescription.Length > 0 ? string.Join("\n", Settings.RaidEmbedDescription) : "";
                if (description.Length > 4096)
                    description = description[..4096];

                var bytes = Settings.TakeScreenshot ? await SwitchConnection.Screengrab(token).ConfigureAwait(false) : Array.Empty<byte>();
                var embed = new EmbedBuilder()
                {
                    Title = disband ? "**Raid was disbanded due to a banned user**" : title,
                    Description = disband ? message : description,
                    Color = disband ? Color.Red : hatTrick ? Color.Purple : Color.Green,
                    ImageUrl = bytes.Length > 0 ? "attachment://zap.jpg" : "",
                }.WithFooter(new EmbedFooterBuilder()
                {
                    Text = $"Host: {HostSAV.OT} | Uptime: {StartTime - DateTime.Now:d\\.hh\\:mm\\:ss}\n" +
                           $"Raids: {RaidCount} | Wins: {WinCount} | Losses: {LossCount}\n" +
                           $"Powered By: RaidBotSV - {RaidBotVersion}",
                });

                if (!disband && names is null)
                {
                    var code = $"**{(Settings.CodeTheRaid ? await GetRaidCode(token).ConfigureAwait(false) : "Free For All")}**";
                    embed.AddField("**Waiting in lobby!**", $"Raid code: {code}");
                }

                if (!disband && names is not null)
                {
                    var players = string.Empty;
                    int i = 2;
                    names.ForEach(x =>
                    {
                        players += $"Player {i} - **{x}**\n";
                        i++;
                    });

                    embed.AddField($"**Raid #{RaidCount} is starting!**", players);
                }

                EmbedQueue.Enqueue((bytes, embed));
            }
        }

        // From PokeTradeBotSV, modified.
        private async Task<bool> ConnectToOnline(PokeTradeHubConfig config, CancellationToken token)
        {
            if (await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
                return true;

            await Click(X, 3_000, token).ConfigureAwait(false);
            await Click(L, 5_000 + config.Timings.ExtraTimeConnectOnline, token).ConfigureAwait(false);

            // Try one more time.
            if (!await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
            {
                Log("Failed to connect the first time, trying again...");
                await RecoverToOverworld(token).ConfigureAwait(false);
                await Click(X, 3_000, token).ConfigureAwait(false);
                await Click(L, 5_000 + config.Timings.ExtraTimeConnectOnline, token).ConfigureAwait(false);
            }

            var wait = 0;
            while (!await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
            {
                await Task.Delay(0_500, token).ConfigureAwait(false);
                if (++wait > 30) // More than 15 seconds without a connection.
                    return false;
            }

            // There are several seconds after connection is established before we can dismiss the menu.
            await Task.Delay(3_000 + config.Timings.ExtraTimeConnectOnline, token).ConfigureAwait(false);
            await Click(A, 1_000, token).ConfigureAwait(false);
            return true;
        }

        // From PokeTradeBotSV.
        private async Task<bool> RecoverToOverworld(CancellationToken token)
        {
            if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                return true;

            Log("Attempting to recover to overworld.");
            var attempts = 0;
            while (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
            {
                attempts++;
                if (attempts >= 30)
                    break;

                await Click(B, 1_300, token).ConfigureAwait(false);
                if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                    break;

                await Click(B, 2_000, token).ConfigureAwait(false);
                if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                    break;

                await Click(A, 1_300, token).ConfigureAwait(false);
                if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                    break;
            }

            // We didn't make it for some reason.
            if (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
            {
                Log("Failed to recover to overworld, rebooting the game.");
                await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
            }
            await Task.Delay(1_000, token).ConfigureAwait(false);
            return true;
        }
    }
}