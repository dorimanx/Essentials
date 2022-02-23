using Newtonsoft.Json;
using NLog;
using Sandbox.Engine.Multiplayer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using VRage.GameServices;
using VRage.Network;
using VRageMath;

namespace Essentials
{
    public class PlayerAccountModule
    {
        public static List<PlayerAccountData> PlayersAccounts = new List<PlayerAccountData>();
        public RanksAndPermissionsModule RanksAndPermissions = new RanksAndPermissionsModule();
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public class PlayerAccountData
        {
            [JsonProperty(Order = 1)]
            public string Player { get; set; }
            [JsonProperty(Order = 2)]
            public ulong SteamID { get; set; }

            [JsonProperty(Order = 3)]
            public long IdentityID { get; set; } = 0L;

            [JsonProperty(Order = 4)]
            public string Rank { get; set; } = "Default";

            [JsonProperty(Order = 5)]
            public List<string> KnownIps = new List<string>();

            [JsonProperty(Order = 6)]
            public DiscordData DiscordData = new DiscordData();

            [JsonProperty(Order = 7)]
            public RanksAndPermissionsModule.Permissions Permissions = new RanksAndPermissionsModule.Permissions();

            [JsonProperty(Order = 8)]
            public Dictionary<string, Vector3D> Homes { get; set; } = new Dictionary<string, Vector3D>();
        }


        public class DiscordData
        {
            [JsonProperty(Order = 1)]
            public string DiscordName { get; set; }

            [JsonProperty(Order = 2)]
            public ulong DiscordID { get; set; } = 0L;

            [JsonProperty(Order = 3)]
            public Dictionary<ulong, string> Roles { get; set; } = new Dictionary<ulong, string>();
        }

        public void UpdatePlayerAccount(PlayerAccountData obj)
        {
            var objectToRepalce = PlayersAccounts.Where(i => i.SteamID == obj.SteamID).First();
            var index = PlayersAccounts.IndexOf(objectToRepalce);
            if (index != -1)
                PlayersAccounts[index] = obj;
            SaveAccountData();
        }

        public void UpdatePlayerAccount(List<PlayerAccountData> PlayerObjects)
        {
            foreach (PlayerAccountData Account in PlayerObjects.ToList())
            {
                UpdatePlayerAccount(Account);
            }
        }

        public void ValidateRanks()
        {
            Dictionary<RanksAndPermissionsModule.RankData, List<string>> InheritsToRemove = new Dictionary<RanksAndPermissionsModule.RankData, List<string>>(); ;
            foreach (RanksAndPermissionsModule.RankData rank in RanksAndPermissionsModule.Ranks)
            {
                List<string> ValuesToRemove = new List<string>();
                foreach (string inheritedRank in rank.Inherits)
                {
                    if (RanksAndPermissions.GetRankData(inheritedRank) == null && !ValuesToRemove.Contains(inheritedRank))
                    {
                        ValuesToRemove.Add(inheritedRank);
                    }
                }
                if (ValuesToRemove.Count > 0)
                {
                    Log.Error($"The rank '{rank.RankName}' was found to have {ValuesToRemove.Count} Non-existing rank(s) in its 'Inherits' List... Queuing invalid values for removal!");
                    InheritsToRemove.Add(rank, ValuesToRemove);
                }
            }

            if (InheritsToRemove.Count > 0)
            {
                Log.Info("Removing mis-configured values...");
                foreach (var data in InheritsToRemove)
                {
                    foreach (string InheritedRank in data.Value)
                    {
                        data.Key.Inherits.Remove(InheritedRank);
                    }
                    RanksAndPermissions.UpdateRankObject(data.Key);
                }
                Log.Info("Removed mis-configured values!");
            }

            Log.Info("Validating player ranks");
            List<PlayerAccountData> PlayerObjectsToUpdate = new List<PlayerAccountData>();
            foreach (PlayerAccountData Player in PlayersAccounts.ToList())
            {
                if (RanksAndPermissions.GetRankData(Player.Rank) == null)
                {
                    Log.Error($"{Player.Player} does not have a valid rank... Setting to default! ({EssentialsPlugin.Instance.Config.DefaultRank})");
                    Player.Rank = EssentialsPlugin.Instance.Config.DefaultRank;
                    PlayerObjectsToUpdate.Add(Player);

                    UpdatePlayerAccount(Player);
                }
            }
        }

        public void SaveAccountData()
        {
            File.WriteAllText(EssentialsPlugin.Instance.homeDataPath, JsonConvert.SerializeObject(PlayersAccounts, Formatting.Indented));
        }

        public static void InsertDiscord(ulong steamID, string discordID, string discordName, Dictionary<ulong, string> RoleData)
        {
            if (steamID == 0)
            {
                Log.Info($"Cannont Insert Discord, SteamId is 0");
                return;
            }

            Log.Info($"DiscordID for {steamID} received from SEDB!... Inserting into player account ({discordID})");
            var AccModule = new PlayerAccountModule();
            var account = AccModule.GetAccount(steamID);

            account.DiscordData.DiscordID = ulong.Parse(discordID);
            account.DiscordData.DiscordName = discordName;
            foreach (var role in RoleData)
            {
                if (account.DiscordData.DiscordID == ulong.Parse(discordID))
                {
                    account.DiscordData.DiscordName = discordName;
                    if (!account.DiscordData.Roles.ContainsKey(role.Key))
                    {
                        account.DiscordData.Roles.Add(role.Key, role.Value);
                    }
                }
            }

            AccModule.UpdatePlayerAccount(account);
        }

        public void GenerateAccount(Torch.API.IPlayer player)
        {
            try
            {
                ulong steamid = 0;
                var steamid_backup = MyEventContext.Current.Sender.Value;
                System.Net.IPAddress ip;

                if (player == null || player.SteamId == 0)
                    steamid = steamid_backup;
                else
                    steamid = player.SteamId;

                if (steamid != 0)
                {
                    var state = new MyP2PSessionState();
                    Sandbox.Engine.Networking.MyGameService.Peer2Peer.GetSessionState(steamid, ref state);
                    ip = new IPAddress(BitConverter.GetBytes(state.RemoteIP).Reverse().ToArray());
                    if (ip == null)
                        return;
                }
                else
                    ip = null;

                // Disconnect client with Ip 0.0.0.0 to prevent SteamLib Crash.
                if (ip != null)
                {
                    if (ip.ToString() == "0.0.0.0")
                    {
                        MyMultiplayer.Static.DisconnectClient(steamid);
                        Log.Info($"Detected IP: {ip} from SteamID {steamid} Client was disconnected");
                    }
                }

                PlayerAccountData data = new PlayerAccountData();
                bool found = false;

                foreach (var Account in PlayersAccounts)
                {
                    if (Account.SteamID == steamid)
                    {
                        if (!Account.KnownIps.Contains(ip.ToString()) && ip.ToString() != "0.0.0.0")
                            Account.KnownIps.Add(ip.ToString());

                        if (Account.IdentityID == 0L && !string.IsNullOrEmpty(Account.Player))
                        {
                            Account.IdentityID = Utilities.GetIdentityByNameOrIds(Account.Player)?.IdentityId ?? 0L;
                            UpdatePlayerAccount(Account);
                        }

                        if (ip != null)
                        {
                            if (!Account.KnownIps.Contains(ip.ToString()) && ip.ToString() != "0.0.0.0")
                            {
                                if (Account != null && Account.KnownIps != null)
                                    Account.KnownIps.Add(ip.ToString());
                                else
                                    Log.Info($"No account KnowIps exist for IP: {ip}");
                            }
                        }
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    if (player == null)
                    {
                        Log.Info($"Cannont Create new account Player is Null");
                        return;
                    }

                    Log.Info($"Creating new account object for {player.Name}");
                    if (steamid != 0)
                        data.SteamID = steamid;
                    else
                        data.SteamID = steamid_backup;
                    data.Player = player.Name;
                    data.Rank = EssentialsPlugin.Instance.Config.DefaultRank;
                    if (ip != null)
                        data.KnownIps.Add(ip.ToString());
                    PlayersAccounts.Add(data);
                    SaveAccountData();
                    return;
                }
                if (steamid == 0)
                    Log.Info($"Cannont Create new account for player, no steamid or IP detected, maybe next time.");
            }
            catch
            {
                Log.Error($"Creating NEW account for {player.Name}, Player was not found in List.");
            }
        }

        public void CheckIp(Torch.API.IPlayer Player)
        {
            if (Player == null)
            {
                Log.Info($"Cannont check for IP, player in Null");
                return;
            }

            ulong steamid = Player.SteamId;
            var steamid_backup = MyEventContext.Current.Sender.Value;

            if (steamid == 0)
                steamid = steamid_backup;

            var state = new MyP2PSessionState();
            Sandbox.Engine.Networking.MyGameService.Peer2Peer.GetSessionState(steamid, ref state);
            var ip = new IPAddress(BitConverter.GetBytes(state.RemoteIP).Reverse().ToArray());

            foreach (var account in PlayersAccounts)
            {
                if (account.KnownIps.Contains(ip.ToString()) && account.Player != Player.Name)
                {
                    Log.Warn($"WARNING! {Player.Name} shares the same IP address as {account.Player}");
                }
            }
        }

        public string GetRank(ulong steamID)
        {
            PlayerAccountData data = new PlayerAccountData();
            foreach (var Account in PlayersAccounts)
            {
                if (Account.SteamID == steamID)
                {
                    data = Account;
                    break;
                }
            }
            return data.Rank;
        }

        public PlayerAccountData GetAccount(ulong steamID)
        {
            _ = new PlayerAccountData();
            PlayerAccountData data = null;
            foreach (var Account in PlayersAccounts)
            {
                if (Account.SteamID == steamID)
                {
                    data = Account;
                    break;
                }
            }
            return data;
        }
    }
}
