using NLog;
using Sandbox.Game.Entities;
using System;
using System.Linq;
using System.Text;
using Torch.Commands;
using Torch.Commands.Permissions;
using Torch.Mod;
using Torch.Mod.Messages;

namespace Essentials.Commands
{
    [Category("cleanup")]
    public class CleanupModule : CommandModule
    {
        private static readonly Logger Log = LogManager.GetLogger("Essentials");

        [Command("scan", "Find grids matching the given conditions")]
        [Permission(MyPromoteLevel.SpaceMaster)]
        public void Scan()
        {
            var count = ConditionsChecker.ScanConditions(Context, Context.Args).Count();
            Context.Respond($"Found {count} grids matching the given conditions.");
        }

        [Command("list", "Lists grids matching the given conditions")]
        public void List()
        {
            var grids = ConditionsChecker.ScanConditions(Context, Context.Args).OrderBy(g => g.DisplayName).ToList();
            if (Context.SentBySelf)
            {
                Context.Respond(String.Join("\n", grids.Select((g, i) => $"{i + 1}. {grids[i].DisplayName} ({grids[i].BlocksCount} block(s))")));
                Context.Respond($"Found {grids.Count} grids matching the given conditions.");
            }
            else
            {
                var m = new DialogMessage("Cleanup", null, $"Found {grids.Count} matching", String.Join("\n", grids.Select((g, i) => $"{i + 1}. {grids[i].DisplayName} ({grids[i].BlocksCount} block(s))")));
                ModCommunication.SendMessageTo(m, Context.Player.SteamUserId);
            }
        }

        [Command("delete", "Delete grids matching the given conditions")]
        [Permission(MyPromoteLevel.SpaceMaster)]
        public void Delete()
        {
            try

            {
            	var count = 0;
            	foreach (var grid in ConditionsChecker.ScanConditions(Context, Context.Args))
                {
                    if (grid != null && !grid.MarkedForClose && !grid.Closed)
                    {
                        if (grid.EntityId != 0L && grid.DisplayName != null)
                            Log.Info($"Deleting grid: {grid.EntityId}: {grid.DisplayName}");

                        EjectPilots(grid);
                        grid.Close();
                        count++;
                    }
                }
                Context.Respond($"Deleted {count} grids matching the given conditions.");
                Log.Info($"Cleanup deleted {count} grids matching conditions {string.Join(", ", Context.Args)}");
            }
            catch
            {
                Log.Info($"Cleanup Delete Failed, Server Crash was avoided.");
            }
        }

        [Command("delete floatingobjects", "deletes floating objects")]
        [Permission(MyPromoteLevel.SpaceMaster)]
        public void FlObjDelete()
        {
            try
            {
                var count = 0;
                foreach (var floater in MyEntities.GetEntities().OfType<MyFloatingObject>())
                {
                    if (floater != null)
                    {
                        if (floater.DisplayName != null)
                            Log.Info($"Deleting floating object: {floater.DisplayName}");
                        floater.Close();
                        count++;
                    }
                }
                Context.Respond($"Deleted {count} floating objects.");
                Log.Info($"Cleanup deleted {count} floating objects");
            }
            catch
            {
                Log.Info($"Cleanup floatingobjects Failed! Server Crash was avoided");
            }
        }

        [Command("help", "Lists all cleanup conditions.")]
        public void Help()
        {
            var sb = new StringBuilder();
            foreach (var c in ConditionsChecker.GetAllConditions())
            {
                sb.AppendLine($"{c.Command}{(string.IsNullOrEmpty(c.InvertCommand) ? string.Empty : $" ({c.InvertCommand})")}:");
                sb.AppendLine($"   {c.HelpText}");
            }

            if (!Context.SentBySelf)
                ModCommunication.SendMessageTo(new DialogMessage("Cleanup help", null, sb.ToString()), Context.Player.SteamUserId);
            else
                Context.Respond(sb.ToString());
        }

        /// <summary>
        /// Removes pilots from grid before deleting,
        /// so the character doesn't also get deleted and break everything
        /// </summary>
        /// <param name="grid"></param>
        public void EjectPilots(MyCubeGrid grid)
        {
            foreach (var c in grid.GetFatBlocks<MyCockpit>())
            {
                if (c != null && c.Pilot != null)
                    c.RemovePilot();
            }
        }
    }
}
