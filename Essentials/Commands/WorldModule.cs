using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using Sandbox.ModAPI;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Torch.Commands;
using Torch.Mod;
using Torch.Mod.Messages;
using Torch.Commands.Permissions;
using Torch.Managers;
using Torch.Utils;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Network;
using Sandbox.Engine.Multiplayer;

namespace Essentials.Commands
{
    public class WorldModule : CommandModule
    {
        private static readonly FieldInfo GpsDicField = typeof(MyGpsCollection).GetField("m_playerGpss", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo SeedParamField = typeof(MyProceduralWorldGenerator).GetField("m_existingObjectsSeeds", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo CamerasField = typeof(MySession).GetField("Cameras", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo AllCamerasField = CamerasField.FieldType.GetField("m_entityCameraSettings", BindingFlags.NonPublic | BindingFlags.Instance);

#pragma warning disable CS0649 // is never assigned to, and will always have its default value null
#pragma warning disable IDE0044 // Add readonly modifier
        [ReflectedGetter(Name = "m_relationsBetweenFactions", Type = typeof(MyFactionCollection))]
        private static Func<MyFactionCollection, Dictionary<MyFactionCollection.MyRelatablePair, Tuple<MyRelationsBetweenFactions, int>>> _relationsGet;
        [ReflectedGetter(Name = "m_relationsBetweenPlayersAndFactions", Type = typeof(MyFactionCollection))]
        private static Func<MyFactionCollection, Dictionary<MyFactionCollection.MyRelatablePair, Tuple<MyRelationsBetweenFactions, int>>> _playerRelationsGet;
#pragma warning restore IDE0044 // Add readonly modifier
#pragma warning restore CS0649 // is never assigned to, and will always have its default value null

        [Command("identity clean", "Remove identities that have not logged on in X days.")]
        [Permission(MyPromoteLevel.SpaceMaster)]
        public void CleanIdentities(int days)
        {
            var count = 0;
            var idents = MySession.Static.Players.GetAllIdentities().ToList();
            var cutoff = DateTime.Now - TimeSpan.FromDays(days);
            var removeIds = new List<MyIdentity>();

            CleanGPSSandbox(days);
            CleanSandboxBankAccounts(days);

            foreach (var identity in idents.Where(identity => identity.LastLoginTime < cutoff))
            {
                if (MySession.Static.Players.IdentityIsNpc(identity.IdentityId))
                    continue;

                var PlayerSteamID = MySession.Static.Players.TryGetSteamId(identity.IdentityId);

                if (PlayerSteamID > 0 && MySession.Static.IsUserAdmin(PlayerSteamID))
                    continue;

                count++;
                removeIds.Add(identity);
            }

            FixGridOwnership(new List<long>(removeIds.Select(x => x.IdentityId)), false);
            RemoveEmptyFactions();
            Context.Respond($"Removed {count} old identities");
        }

        [Command("identity purge", "Remove identities AND the grids they own if they have not logged on in X days.")]
        [Permission(MyPromoteLevel.SpaceMaster)]
        public void PurgeIdentities(int days)
        {
            var count = 0;
            var count2 = 0;
            var removeIds = new List<MyIdentity>();
            var idents = MySession.Static.Players.GetAllIdentities().ToList();
            var cutoff = DateTime.Now - TimeSpan.FromDays(days);

            CleanGPSSandbox(days);
            CleanSandboxBankAccounts(days);

            foreach (var identity in idents.Where(identity => identity.LastLoginTime < cutoff))
            {
                if (identity == null || MySession.Static.Players.IdentityIsNpc(identity.IdentityId))
                    continue;

                var PlayerSteamID = MySession.Static.Players.TryGetSteamId(identity.IdentityId);

                if (PlayerSteamID > 0 && MySession.Static.IsUserAdmin(PlayerSteamID))
                    continue;

                count++;
                removeIds.Add(identity);
            }

            if (count == 0)
            {
                Context.Respond($"No old identity found past {days}");
                return;
            }

            count2 = FixGridOwnership(new List<long>(removeIds.Select(x => x.IdentityId)));
            RemoveFromFaction_Internal(removeIds);

            RemoveEmptyFactions();
            Context.Respond($"Removed {count} old identities and {count2} grids owned by them.");
        }

        [Command("identity clear", "Clear identity of specific player")]
        [Permission(MyPromoteLevel.Admin)]
        public void PurgeIdentity(string playername)
        {
            int count;
            var playerGpss = GpsDicField.GetValue(MySession.Static.Gpss) as Dictionary<long, Dictionary<int, MyGps>>;
            var id = Utilities.GetIdentityByNameOrIds(playername);

            if (id == null)
            {
                Context.Respond($"No Identity found for {playername}.  Try Again");
                return;
            }

            var PlayerSteamID = MySession.Static.Players.TryGetSteamId(id.IdentityId);

            if (PlayerSteamID > 0 && !MySession.Static.IsUserAdmin(PlayerSteamID))
                MyBankingSystem.RemoveAccount_Clients(id.IdentityId);

            if (playerGpss.ContainsKey(id.IdentityId))
                playerGpss.Remove(id.IdentityId);

            count = FixGridOwnership(new List<long> { id.IdentityId });
            RemoveEmptyFactions();
            Context.Respond($"Removed identity and {count} grids owned by them, also bank account and GPS Inventory.");
        }

        [Command("rep wipe", "Resets the reputation on the server")]
        [Permission(MyPromoteLevel.Admin)]
        public void WipeReputation(bool removePlayerToFaction = true, bool removeFactionToFaction = true)
        {
            var count = WipeRep(removePlayerToFaction, removeFactionToFaction);
            Context.Respond($"Wiped {count} reputations");
        }

        [Command("faction clean", "Removes factions with fewer than the given number of players.")]
        [Permission(MyPromoteLevel.SpaceMaster)]
        public void CleanFactions(int memberCount = 1)
        {
            int count = CleanFaction_Internal(memberCount);
            Context.Respond($"Removed {count} factions with fewer than {memberCount} members.");
        }

        [Command("faction remove", "removes faction by tag name")]
        [Permission(MyPromoteLevel.Admin)]
        public void RemoveFaction(string tag)
        {
            if (tag == null)
            {
                Context.Respond("You need to add a faction tag to remove");
                return;
            }

            var fac = MySession.Static.Factions.TryGetFactionByTag(tag);
            if (fac == null || !MySession.Static.Factions.FactionTagExists(tag))
            {
                Context.Respond($"{tag} is not a faction on this server");
                return;
            }

            foreach (var player in fac.Members)
            {
                if (!MySession.Static.Players.HasIdentity(player.Key)) continue;
                fac.KickMember(player.Key);
            }

            RemoveFaction(fac);
            Context.Respond(MySession.Static.Factions.FactionTagExists(tag)
                ? $"{tag} removal failed"
                : $"{tag} removal successful");
        }

        [Command("faction info", "lists members of given faction")]
        [Permission(MyPromoteLevel.Admin)]
        public void FactionInfo()
        {
            StringBuilder sb = new StringBuilder();

            foreach (var factionID in MySession.Static.Factions)
            {
                double memberCount;
                var faction = factionID.Value;
                memberCount = faction.Members.Count();
                sb.AppendLine();
                if (faction.IsEveryoneNpc())
                {
                    sb.AppendLine($"{faction.Tag} - {memberCount} NPC found in this faction");
                    continue;
                }

                sb.AppendLine($"{faction.Tag} - {memberCount} players in this faction");
                foreach (var player in faction?.Members)
                {
                    if (!MySession.Static.Players.HasIdentity(player.Key) && !MySession.Static.Players.IdentityIsNpc(player.Key) ||
                        string.IsNullOrEmpty(MySession.Static?.Players?.TryGetIdentity(player.Value.PlayerId).DisplayName)) continue; //This is needed to filter out players with no id.
                    sb.AppendLine($"{MySession.Static?.Players?.TryGetIdentity(player.Value.PlayerId).DisplayName}");
                }
            }

            if (Context.Player == null)
                Context.Respond(sb.ToString());
            else if (Context?.Player?.SteamUserId > 0)
                ModCommunication.SendMessageTo(new DialogMessage("Faction Info", null, sb.ToString()), Context.Player.SteamUserId);
        }

        [Command("faction lock", "Lock free access to factions")]
        [Permission(MyPromoteLevel.Admin)]
        public void FactionLock()
        {
            var LockedFaction = 0;

            foreach (var factionID in MySession.Static.Factions)
            {
                if (factionID.Value.AutoAcceptMember)
                {
                    factionID.Value.AutoAcceptMember = false;
                    LockedFaction += 1;
                }
            }

            if (Context.Player == null)
                Context.Respond($"Locked {LockedFaction} Open Factions");
            else if (Context?.Player?.SteamUserId > 0)
                ModCommunication.SendMessageTo(new DialogMessage("Faction Lock", null, $"Locked {LockedFaction} Open Factions"), Context.Player.SteamUserId);
        }

        private static void RemoveEmptyFactions()
        {
            CleanFaction_Internal(1);
        }

        private static int CleanFaction_Internal(int memberCount = 1)
        {
            int result = 0;

            foreach (var faction in MySession.Static.Factions.ToList())
            {
                if ((faction.Value.IsEveryoneNpc() || !faction.Value.AcceptHumans) && faction.Value.Members.Count != 0) //needed to add this to catch the 0 member factions
                    continue;

                int validmembers = 0;
                //O(2n)
                foreach (var member in faction.Value.Members)
                {
                    if (!MySession.Static.Players.HasIdentity(member.Key) && !MySession.Static.Players.IdentityIsNpc(member.Key))
                        continue;

                    validmembers++;
                    if (validmembers >= memberCount)
                        break;
                }

                if (validmembers >= memberCount)
                    continue;

                RemoveFaction(faction.Value);
                result++;
            }
            return result;
        }

        private static void RemoveFromFaction_Internal(List<MyIdentity> Ids)
        {
            foreach (var identity in Ids)
            {
                RemoveFromFaction_Internal(identity);
                MySession.Static.Players.RemoveIdentity(identity.IdentityId);
            }
        }

        private static bool RemoveFromFaction_Internal(MyIdentity identity)
        {
            var fac = MySession.Static.Factions.GetPlayerFaction(identity.IdentityId);
            if (fac == null)
                return false;

            /* 
             * VisualScriptLogicProvider takes care of removal of faction if last 
             * identity is kicked, and promotes the next player in line to Founder 
             * if the founder is being kicked. 
             * 
             * Factions must have a founder otherwise calls like MyFaction.Members.Keys will NRE. 
             */
            MyVisualScriptLogicProvider.KickPlayerFromFaction(identity.IdentityId);

            return true;
        }

#pragma warning disable IDE0044 // Add readonly modifier
        private static MethodInfo _factionChangeSuccessInfo = typeof(MyFactionCollection).GetMethod("FactionStateChangeSuccess", BindingFlags.NonPublic | BindingFlags.Static);
#pragma warning restore IDE0044 // Add readonly modifier

        //TODO: This should probably be moved into Torch base, but I honestly cannot be bothered
        /// <summary>
        /// Removes a faction from the server and all clients because Keen fucked up their own system.
        /// </summary>
        /// <param name="faction"></param>
        private static void RemoveFaction(MyFaction faction)
        {
            //bypass the check that says the server doesn't have permission to delete factions
            //_applyFactionState(MySession.Static.Factions, MyFactionStateChange.RemoveFaction, faction.FactionId, faction.FactionId, 0L, 0L);
            //MyMultiplayer.RaiseStaticEvent(s =>
            //        (Action<MyFactionStateChange, long, long, long, long>) Delegate.CreateDelegate(typeof(Action<MyFactionStateChange, long, long, long, long>), _factionStateChangeReq),
            //    MyFactionStateChange.RemoveFaction, faction.FactionId, faction.FactionId, faction.FounderId, faction.FounderId);
            NetworkManager.RaiseStaticEvent(_factionChangeSuccessInfo, MyFactionStateChange.RemoveFaction, faction.FactionId, faction.FactionId, 0L, 0L);

            if (!MyAPIGateway.Session.Factions.FactionTagExists(faction.Tag))
                return;

            MyAPIGateway.Session.Factions.RemoveFaction(faction.FactionId); //Added to remove factions that got through the crack
        }

        private static int FixGridOwnership(List<long> Ids, bool deleteGrids = true)
        {
            if (Ids.Count == 0) return 0;
            var grids = new List<MyCubeGrid>(MyEntities.GetEntities().OfType<MyCubeGrid>());
            int count = 0;
            foreach (var id in Ids)
            {
                if (id == 0) continue;
                foreach (var grid in grids.Where(grid => grid.BigOwners.Contains(id)))
                {
                    if (grid.BigOwners.Count > 1)
                    {
                        var newOwnerId = grid.BigOwners.FirstOrDefault(x => x != id);
                        grid.TransferBlocksBuiltByID(id, newOwnerId);
                        foreach (var gridCubeBlock in grid.CubeBlocks.Where(x => x.OwnerId == id))
                        {
                            grid.ChangeOwner(gridCubeBlock.FatBlock, id, newOwnerId);
                        }
                        grid.RecalculateOwners();
                        continue;
                    }
                    if (deleteGrids) grid.Close();
                    count++;
                }
            }

            return count;

        }

        private static int FixBlockOwnership()
        {
            int count = 0;
            foreach (var entity in MyEntities.GetEntities())
            {
                if (!(entity is MyCubeGrid grid))
                    continue;

                var owner = grid.BigOwners.FirstOrDefault();
                var share = owner == 0 ? MyOwnershipShareModeEnum.All : MyOwnershipShareModeEnum.Faction;
                foreach (var block in grid.GetFatBlocks())
                {
                    if (block.OwnerId == 0 || MySession.Static.Players.HasIdentity(block.OwnerId))
                        continue;

                    block.ChangeOwner(owner, share);
                    count++;
                }
            }
            return count;
        }

        [Command("ainpc clean", "Cleans up NPC junk data from the sandbox file")]
        [Permission(MyPromoteLevel.Admin)]
        public void AI_NPC_Clean()
        {
            int count = 0;
            var validIdentities = new HashSet<long>();
            var idCache = new HashSet<long>();

            //find all identities owning a block
            foreach (var entity in MyEntities.GetEntities())
            {
                if (!(entity is MyCubeGrid grid))
                    continue;

                validIdentities.UnionWith(grid.SmallOwners);
            }

            foreach (var online in MySession.Static.Players.GetOnlinePlayers())
            {
                validIdentities.Add(online.Identity.IdentityId);
            }

            //might not be necessary, but just in case
            validIdentities.Remove(0);

            List<string> npc_model = new List<string> { "Shadow_Bot", "Space_Zombie", "Drone_Bot", "Alien_OB", "Mutant" };

            foreach (var identity in MySession.Static.Players.GetAllIdentities().ToList())
            {
                if (npc_model.Contains(identity.Model)) // Доп проверки для НПС не реализуем т.к. чистим всех
                {
                    RemoveFromFaction_Internal(identity);

                    // Две строчки ниже по ощущениям тот еще костыль
                    MySession.Static.Players.TryGetPlayerId(identity.IdentityId, out MyPlayer.PlayerId player_id);

                    if (MySession.Static.Players.TryGetPlayerById(player_id, out MyPlayer player))
                    {
                        MySession.Static.Players.RemovePlayer(player, true);
                        count++;
                    }

                    MySession.Static.Players.RemoveIdentity(identity.IdentityId, default);
                    validIdentities.Remove(identity.IdentityId); // Удаляем айдишник НПС из списка валидных чтобы почистило репу и остальной мусор
                    count++;
                }
            }

            //reset ownership of blocks belonging to deleted identities
            count += FixBlockOwnership();

            //clean up empty factions
            count += CleanFaction_Internal();

            //cleanup reputations
            count += CleanupReputations();

            //Keen, for the love of god why is everything about GPS internal.
            var playerGpss = GpsDicField.GetValue(MySession.Static.Gpss) as Dictionary<long, Dictionary<int, MyGps>>;
            foreach (var id in playerGpss.Keys)
            {
                if (!validIdentities.Contains(id))
                    idCache.Add(id);
            }

            foreach (var id in idCache)
                playerGpss.Remove(id);

            count += idCache.Count;
            idCache.Clear();

            Context.Respond($"Removed {count} unnecessary AI-NPC elements.");
        }

        [Command("sandbox clean", "Cleans up junk data from the sandbox file")]
        [Permission(MyPromoteLevel.Admin)]
        public void CleanSandbox()
        {
            int count = 0;
            var validIdentities = new HashSet<long>();
            var idCache = new HashSet<long>();

            //find all identities owning a block
            foreach (var entity in MyEntities.GetEntities())
            {
                if (!(entity is MyCubeGrid grid))
                    continue;

                validIdentities.UnionWith(grid.SmallOwners);
            }

            foreach (var online in MySession.Static.Players.GetOnlinePlayers())
            {
                validIdentities.Add(online.Identity.IdentityId);
            }

            //might not be necessary, but just in case
            validIdentities.Remove(0);

            List<string> npc_model = new List<string> {"Shadow_Bot", "Space_Zombie", "Drone_Bot", "Alien_OB", "Mutant"};

            foreach (var identity in MySession.Static.Players.GetAllIdentities().ToList())
            {
                if (npc_model.Contains(identity.Model)) // Доп проверки для НПС не реализуем т.к. чистим всех
                {
                    RemoveFromFaction_Internal(identity);

                    // Две строчки ниже по ощущениям тот еще костыль
                    MySession.Static.Players.TryGetPlayerId(identity.IdentityId, out MyPlayer.PlayerId player_id);

                    if (MySession.Static.Players.TryGetPlayerById(player_id, out MyPlayer player))
                    {
                        MySession.Static.Players.RemovePlayer(player, true);
                        count++;
                    }

                    MySession.Static.Players.RemoveIdentity(identity.IdentityId, default);
                    validIdentities.Remove(identity.IdentityId); // Удаляем айдишник НПС из списка валидных чтобы почистило репу и остальной мусор
                    count++;
                }
                else
                {
                    //clean identities that don't own any blocks, or don't have a steam ID for whatever reason
                	if (MySession.Static.Players.IdentityIsNpc(identity.IdentityId) || string.IsNullOrEmpty(identity.DisplayName))
                	{
                    	validIdentities.Add(identity.IdentityId);
                    	continue;
                	}

                	if (validIdentities.Contains(identity.IdentityId))
                    	continue;

                	RemoveFromFaction_Internal(identity);
                    MySession.Static.Players.RemoveIdentity(identity.IdentityId, default);
                	validIdentities.Remove(identity.IdentityId);
                	count++;
                }
            }

            //reset ownership of blocks belonging to deleted identities
            count += FixBlockOwnership();

            //clean up empty factions
            count += CleanFaction_Internal();

            //cleanup reputations
            count += CleanupReputations();

            //Keen, for the love of god why is everything about GPS internal.
            var playerGpss = GpsDicField.GetValue(MySession.Static.Gpss) as Dictionary<long, Dictionary<int, MyGps>>;
            foreach (var id in playerGpss.Keys)
            {
                if (!validIdentities.Contains(id))
                    idCache.Add(id);
            }

            foreach (var id in idCache)
                playerGpss.Remove(id);

            count += idCache.Count;
            idCache.Clear();

            var g = MySession.Static.GetComponent<MyProceduralWorldGenerator>();
            var f = SeedParamField.GetValue(g) as HashSet<MyObjectSeedParams>;
            count += f.Count;
            f.Clear();

            //TODO
            /*
            foreach (var history in MySession.Static.ChatHistory)
            {
                if (!validIdentities.Contains(history.Key))
                    idCache.Add(history.Key);
            }

            foreach (var id in idCache)
            {
                MySession.Static.ChatHistory.Remove(id);
            }
            count += idCache.Count;
            idCache.Clear();

            //delete chat history for deleted factions
            for (int i = MySession.Static.FactionChatHistory.Count - 1; i >= 0; i--)
            {
                var history = MySession.Static.FactionChatHistory[i];
                if (MySession.Static.Factions.TryGetFactionById(history.FactionId1) == null || MySession.Static.Factions.TryGetFactionById(history.FactionId2) == null)
                {
                    count++;
                    MySession.Static.FactionChatHistory.RemoveAtFast(i);
                }
            }
            */

            var cf = AllCamerasField.GetValue(CamerasField.GetValue(MySession.Static)) as Dictionary<MyPlayer.PlayerId, Dictionary<long, MyEntityCameraSettings>>;
            count += cf.Count;
            cf.Clear();

            Context.Respond($"Removed {count} unnecessary elements.");
        }

        [Command("gps clean", "Cleans up old GPS marks from the sandbox file, for identities that have not logged on in X days")]
        [Permission(MyPromoteLevel.Admin)]
        public void CleanGPSSandbox(int days)
        {
            if (days <= 0)
            {
                Context?.Respond($"Input number of days player have not logged on in");
                return;
            }

            int count = 0;
            int countnotexisting = 0;
            var OldIdentities = new HashSet<long>();
            var AllIdentities = new HashSet<long>();
            var idCache = new HashSet<long>();
            var LostidCache = new HashSet<long>();
            var idents = MySession.Static.Players.GetAllIdentities().ToList();
            var cutoff = DateTime.Now - TimeSpan.FromDays(days);
            var playerGpss = GpsDicField.GetValue(MySession.Static.Gpss) as Dictionary<long, Dictionary<int, MyGps>>;

            foreach (var identity in idents)
            {
                if (identity == null || MySession.Static.Players.IdentityIsNpc(identity.IdentityId))
                    continue;

                var PlayerSteamID = MySession.Static.Players.TryGetSteamId(identity.IdentityId);

                if (PlayerSteamID > 0 && MySession.Static.IsUserAdmin(PlayerSteamID))
                    continue;

                AllIdentities.Add(identity.IdentityId);

                if (identity.LastLoginTime < cutoff)
                    OldIdentities.Add(identity.IdentityId);
            }

            foreach (var id in playerGpss.Keys)
            {
                if (OldIdentities.Count > 0 && OldIdentities.ToList().Contains(id))
                    idCache.Add(id);

                if (AllIdentities.Count > 0 && !AllIdentities.ToList().Contains(id))
                    LostidCache.Add(id);
            }

            // delete requested old gps data for existing Identities
            if (idCache.Count > 0)
            {
                foreach (var id in idCache)
                {
                    if (playerGpss.ContainsKey(id))
                        playerGpss.Remove(id);
                }
            }

            // delete existing old gps data for not existing Identities
            if (LostidCache.Count > 0)
            {
                foreach (var Lostid in LostidCache)
                {
                    if (playerGpss.ContainsKey(Lostid))
                        playerGpss.Remove(Lostid);
                }
            }

            count += idCache.Count;
            countnotexisting += LostidCache.Count;

            idCache.Clear();
            OldIdentities.Clear();
            LostidCache.Clear();
            AllIdentities.Clear();

            Context?.Respond($"Removed {count} old GPS Junk for existing players that have not logged on in {days} days, and {countnotexisting} for no longer existing players.");
        }

        [Command("bank clean", "Cleans up old bank accounts from the sandbox file, for identities that have not logged on in X days")]
        [Permission(MyPromoteLevel.SpaceMaster)]
        public void CleanSandboxBankAccounts(int days)
        {
            if (days <= 0)
            {
                Context?.Respond($"Input number of days player have not logged on in");
                return;
            }

            int count = 0;
            int Lostcount = 0;
            var OldIdentities = new HashSet<long>();
            var AllIdentities = new HashSet<long>();
            var idents = MySession.Static.Players.GetAllIdentities().ToList();
            var cutoff = DateTime.Now - TimeSpan.FromDays(days);

            foreach (var identity in idents)
            {
                if (identity == null || MySession.Static.Players.IdentityIsNpc(identity.IdentityId))
                    continue;

                var PlayerSteamID = MySession.Static.Players.TryGetSteamId(identity.IdentityId);

                if (PlayerSteamID > 0 && MySession.Static.IsUserAdmin(PlayerSteamID))
                    continue;

                AllIdentities.Add(identity.IdentityId);

                if (identity.LastLoginTime < cutoff)
                    OldIdentities.Add(identity.IdentityId);
            }

            if (OldIdentities.Count > 0)
            {
                foreach (var id in OldIdentities.ToList())
                {
                    MyBankingSystem.RemoveAccount_Clients(id);
                    count++;
                }
            }

            if (AllIdentities.Count > 0)
            {
                foreach (var id in AllIdentities.ToList())
                {
                    if (!AllIdentities.ToList().Contains(id))
                    {
                        MyBankingSystem.RemoveAccount_Clients(id);
                        Lostcount++;
                    }
                }
            }

            OldIdentities.Clear();
            AllIdentities.Clear();

            Context?.Respond($"Removed {count} old Bank Accounts for existing players that have not logged on in {days} days, and {Lostcount} for no longer existing players.");
        }

        [Command("reputation clean", "Cleans up old reputations from the sandbox file, for identities that have not logged on in X days")]
        [Permission(MyPromoteLevel.SpaceMaster)]
        public void CleanReputationSandbox(int days)
        {
            if (days <= 0)
            {
                Context?.Respond($"Input number of days player have not logged on in");
                return;
            }

            int count = 0;
            int countnotexisting = 0;
            var OldIdentities = new HashSet<long>();
            var AllIdentities = new HashSet<long>();
            var OldPlayersFactionIDs = new HashSet<long>();
            var AllPlayersFactionIDs = new HashSet<long>();
            var idCache = new HashSet<MyFactionCollection.MyRelatablePair>();
            var LostidCache = new HashSet<MyFactionCollection.MyRelatablePair>();
            var cutoff = DateTime.Now - TimeSpan.FromDays(days);

            var collection0 = _relationsGet(MySession.Static.Factions);
            var collection1 = _playerRelationsGet(MySession.Static.Factions);
            var idents = MySession.Static.Players.GetAllIdentities().ToList();

            foreach (var identity in idents)
            {
                if (identity == null || MySession.Static.Players.IdentityIsNpc(identity.IdentityId))
                    continue;

                var PlayerSteamID = MySession.Static.Players.TryGetSteamId(identity.IdentityId);

                if (PlayerSteamID > 0 && MySession.Static.IsUserAdmin(PlayerSteamID))
                    continue;

                AllIdentities.Add(identity.IdentityId);

                if (identity.LastLoginTime < cutoff)
                    OldIdentities.Add(identity.IdentityId);
            }

            foreach (var PlayerID in OldIdentities)
            {
                var PlayerFaction = MySession.Static.Factions.TryGetPlayerFaction(PlayerID);
                if (PlayerFaction != null && PlayerFaction.FactionId > 0 && PlayerFaction.Members.Count == 1)
                    OldPlayersFactionIDs.Add(PlayerFaction.FactionId);
            }

            foreach (var PlayerID in AllIdentities)
            {
                var PlayerFaction = MySession.Static.Factions.TryGetPlayerFaction(PlayerID);
                if (PlayerFaction != null && PlayerFaction.FactionId > 0 && PlayerFaction.Members.Count == 1)
                    AllPlayersFactionIDs.Add(PlayerFaction.FactionId);
            }

            // Find by Faction to faction list.
            foreach (var pair in collection0.Keys.ToList())
            {
                if (OldPlayersFactionIDs.Count > 0 && OldPlayersFactionIDs.ToList().Contains(pair.RelateeId1))
                    idCache.Add(pair);

                if (AllPlayersFactionIDs.Count > 0 && !AllPlayersFactionIDs.ToList().Contains(pair.RelateeId1))
                    LostidCache.Add(pair);
            }

            // delete requested old reputation data for existing Identities
            if (idCache.Count > 0)
            {
                foreach (var pair in idCache)
                {
                    collection0.Remove(pair);
                }
            }

            // delete existing old reputation data for not existing Identities
            if (LostidCache.Count > 0)
            {
                foreach (var Lostpair in LostidCache)
                {
                    collection0.Remove(Lostpair);
                }
            }

            count += idCache.Count;
            countnotexisting += LostidCache.Count;

            idCache.Clear();
            LostidCache.Clear();

            // Find by Player to Faction List.
            foreach (var pair in collection1.Keys.ToList())
            {
                if (OldIdentities.Count > 0 && OldIdentities.ToList().Contains(pair.RelateeId1))
                    idCache.Add(pair);

                if (AllIdentities.Count > 0 && !AllIdentities.ToList().Contains(pair.RelateeId1))
                    LostidCache.Add(pair);
            }

            // delete requested old reputation data for existing Identities
            if (idCache.Count > 0)
            {
                foreach (var pair in idCache)
                {
                    collection1.Remove(pair);
                }
            }

            // delete existing old reputation data for not existing Identities
            if (LostidCache.Count > 0)
            {
                foreach (var Lostpair in LostidCache)
                {
                    collection1.Remove(Lostpair);
                }
            }

            count += idCache.Count;
            countnotexisting += LostidCache.Count;

            idCache.Clear();
            OldIdentities.Clear();
            LostidCache.Clear();
            AllIdentities.Clear();
            AllPlayersFactionIDs.Clear();
            OldPlayersFactionIDs.Clear();

            Context?.Respond($"Removed {count} old Reputation Junk for existing players that have not logged on in {days} days, and {countnotexisting} for no longer existing players.");
        }

        [Command("oldjunk clean", "Cleans up old reputations/gps/banks from the sandbox file, for identities that have not logged on in X days")]
        [Permission(MyPromoteLevel.SpaceMaster)]
        public void CleanJunkSandbox(int days)
        {
            if (days <= 0)
            {
                Context?.Respond($"Input number of days player have not logged on in");
                return;
            }

            CleanGPSSandbox(days);
            CleanSandboxBankAccounts(days);
            CleanReputationSandbox(days);

            Context?.Respond($"Removed old reputations/gps/banks Junk for players that have not logged on in {days} days");
        }

        [Command("boostnpcrep", "Change repuation between players factions and npc factions for given amount max 1500")]
        [Permission(MyPromoteLevel.Admin)]
        public void BoostNpcReputation(int amount = 2000)
        {
            if (amount > 1500 || amount < -1500)
            {
                Context.Respond("Please input amount in range of -1500 to 1500");
                return;
            }

            var PlayersFactions = new Dictionary<long, MyFaction>();
            var NPCFactions = new Dictionary<long, MyFaction>();
            var idents = MySession.Static.Players.GetAllIdentities().ToList();

            foreach (var FactionItem in MySession.Static.Factions.ToList())
            {
                if (FactionItem.Value.Tag == "SPID" || FactionItem.Value.Tag == "SPRT" ||
                    FactionItem.Value.Tag == "PiratiX" || FactionItem.Value.Tag == "CULT" || FactionItem.Value.Tag == "REAVER")
                    continue;

                if (MySession.Static.Players.IdentityIsNpc(FactionItem.Value.FounderId))
                    FactionItem.Value.AutoAcceptPeace = true;
            }

            foreach (var FactionItem in MySession.Static.Factions.ToList())
            {
                if (FactionItem.Value.Tag == "SPID" || FactionItem.Value.Tag == "SPRT" ||
                    FactionItem.Value.Tag == "PiratiX" || FactionItem.Value.Tag == "CULT" || FactionItem.Value.Tag == "REAVER")
                    continue;

                if (MySession.Static.Players.IdentityIsNpc(FactionItem.Value.FounderId))
                {
                    NPCFactions.Add(FactionItem.Key, FactionItem.Value);
                    continue;
                }

                PlayersFactions.Add(FactionItem.Key, FactionItem.Value);
            }

            int NpcFactionsCount = NPCFactions.Count();
            int PlayerFactionsCount = PlayersFactions.Count();

            foreach (var PlayerFaction in PlayersFactions)
            {
                foreach (var NpcFaction in NPCFactions)
                {
                    if (!MySession.Static.Factions.IsPeaceRequestStateSent(PlayerFaction.Value.FactionId, NpcFaction.Value.FactionId))
                        MyFactionCollection.SendPeaceRequest(PlayerFaction.Value.FactionId, NpcFaction.Value.FactionId);

                    if (MySession.Static.Factions.IsPeaceRequestStatePending(PlayerFaction.Value.FactionId, NpcFaction.Value.FactionId))
                        MyFactionCollection.AcceptPeace(PlayerFaction.Value.FactionId, NpcFaction.Value.FactionId);

                    var AmountNow = MySession.Static.Factions.GetRelationBetweenFactions(PlayerFaction.Value.FactionId, NpcFaction.Value.FactionId);
                    if (AmountNow.Item2 > amount)
                        continue;

                    MySession.Static.Factions.SetReputationBetweenFactions(PlayerFaction.Value.FactionId, NpcFaction.Value.FactionId, amount);
                }
            }

            foreach (var PlayerID in idents)
            {
                foreach (var NpcFaction in NPCFactions)
                {
                    var AmountNow = MySession.Static.Factions.GetRelationBetweenPlayerAndFaction(PlayerID.IdentityId, NpcFaction.Value.FactionId);
                    if (AmountNow.Item2 > amount)
                        continue;

                    _ = MySession.Static.Factions.AddFactionPlayerReputation(PlayerID.IdentityId, NpcFaction.Value.FactionId, amount, ReputationChangeReason.Admin, true, true);
                }
            }

            Context.Respond($"Changed reputation with {NpcFactionsCount} NPC factions to {int.Parse(amount.ToString())} reputation, for {PlayerFactionsCount} player factions, and set peace!");
        }

        private static int WipeRep(bool removePlayerToFaction, bool removeFactionToFaction)
        {
            var result = 0;
            var collection0 = _relationsGet(MySession.Static.Factions);
            var collection1 = _playerRelationsGet(MySession.Static.Factions);

            if (removeFactionToFaction)
            {
                foreach (var pair in collection0.Keys.ToList())
                {
                    collection0.Remove(pair);
                    result++;
                }
            }

            if (removePlayerToFaction)
            {
                foreach (var pair in collection1.Keys.ToList())
                {
                    collection1.Remove(pair);
                    result++;
                }
            }
            return result;
        }

        private static int CleanupReputations()
        {
            var collection = _relationsGet(MySession.Static.Factions);
            var collection2 = _playerRelationsGet(MySession.Static.Factions);
            var validIdentities = new HashSet<long>();

            //find all identities owning a block
            foreach (var entity in MyEntities.GetEntities())
            {
                if (!(entity is MyCubeGrid grid))
                    continue;

                validIdentities.UnionWith(grid.SmallOwners);
            }

            //find online identities
            foreach (var online in MySession.Static.Players.GetOnlinePlayers())
            {
                validIdentities.Add(online.Identity.IdentityId);
            }

            foreach (var identity in MySession.Static.Players.GetAllIdentities().ToList())
            {
                if (MySession.Static.Players.IdentityIsNpc(identity.IdentityId))
                    validIdentities.Add(identity.IdentityId);
            }

            //Add Factions with at least one member to valid identities
            foreach (var faction in MySession.Static.Factions.Factions.Where(x => x.Value.Members.Count > 0))
            {
                validIdentities.Add(faction.Key);
            }

            //might not be necessary, but just in case
            validIdentities.Remove(0);
            var result = 0;
            var collection0List = collection.Keys.ToList();
            var collection1List = collection2.Keys.ToList();

            foreach (var pair in collection0List)
            {
                if (validIdentities.Contains(pair.RelateeId1) && validIdentities.Contains(pair.RelateeId2))
                    continue;
                collection.Remove(pair);
                result++;
            }

            foreach (var pair in collection1List)
            {
                if (validIdentities.Contains(pair.RelateeId1) && validIdentities.Contains(pair.RelateeId2))
                    continue;
                collection2.Remove(pair);
                result++;
            }

            //_relationsSet.Invoke(MySession.Static.Factions,collection);
            //_playerRelationsSet.Invoke(MySession.Static.Factions,collection2);
            return result;
        }
    }
}
