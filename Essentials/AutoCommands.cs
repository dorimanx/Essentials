﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using NLog;
using Torch.Server.ViewModels;
using Sandbox.Game.World;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Entities;
using VRage.Game.ModAPI;
using static Essentials.Gtl;


namespace Essentials
{
    public class AutoCommands : IDisposable
    {
        protected EntityTreeViewModel Tree { get; }

        private static AutoCommands _instance;
        public static AutoCommands Instance => _instance ?? (_instance = new AutoCommands());
        private static readonly Logger Log = LogManager.GetLogger("Essentials");
        private Timer _timer;
        private readonly Dictionary<AutoCommand, DateTime> _simSpeedCheck = new Dictionary<AutoCommand, DateTime>();

        public void Start()
        {
            _timer = new Timer(1000);
            _timer.Elapsed += TimerElapsed;
            _timer.AutoReset = true;
            _timer.Start();
        }

        private bool CanRun(AutoCommand command)
        {
            if (MySession.Static?.Ready == false) return false;

            switch (command.CommandTrigger)
            {
                case Trigger.Disabled:
                    return false;
                case Trigger.OnStart:
                    if (command.Completed || command.IsRunning())break;
                    command.Completed = true;
                    command.RunNow();
                    break;
                case Trigger.Vote:
                    break;
                case Trigger.Timed:
                    return true;
                case Trigger.Scheduled:
                    return true;
                case Trigger.GridCount:
                    var gridCount = MyEntities.GetEntities().OfType<IMyCubeGrid>().Count();
                    switch (command.Compare)
                    {
                        case GreaterThan:
                            return gridCount > command.TriggerCount;
                        case LessThan:
                            return gridCount < command.TriggerCount;
                        case Equal:
                            return Math.Abs(gridCount - command.TriggerCount) < 1;
                        default:
                            throw new Exception("meh");
                    }
                case Trigger.PlayerCount:

                    switch (command.Compare)
                    {
                        case GreaterThan:
                            return Utilities.GetOnlinePlayerCount() > command.TriggerCount;
                        case LessThan:
                            return Utilities.GetOnlinePlayerCount() < command.TriggerCount;
                        case Equal:
                            return Math.Abs(Utilities.GetOnlinePlayerCount() - command.TriggerCount) < 1;
                    }
                    break;

                case Trigger.SimSpeed:
                    var commandActive = _simSpeedCheck.TryGetValue(command, out var time);
                    switch (command.Compare)
                    {
                        case GreaterThan:
                            if (commandActive)
                            {
                                if ((DateTime.Now - time).TotalSeconds < command.TriggerCount) break;
                                _simSpeedCheck.Remove(command);
                                return Math.Min(Sync.ServerSimulationRatio, 1) > command.TriggerRatio;
                            }

                            if (Math.Min(Sync.ServerSimulationRatio, 1) < command.TriggerRatio) break;
                            _simSpeedCheck.Add(command, DateTime.Now);
                            break;

                        case LessThan:
                            if (commandActive)
                            {
                                if ((DateTime.Now - time).TotalSeconds < command.TriggerCount) break;
                                _simSpeedCheck.Remove(command);
                                return Math.Min(Sync.ServerSimulationRatio, 1) < command.TriggerRatio;
                            }

                            if (Math.Min(Sync.ServerSimulationRatio, 1) > command.TriggerRatio) break;
                            _simSpeedCheck.Add(command, DateTime.Now);
                            break;

                        case Equal:
                            if (commandActive)
                            {
                                if ((DateTime.Now - time).TotalSeconds < command.TriggerCount) break;
                                _simSpeedCheck.Remove(command);
                                return (Math.Abs(Sync.ServerSimulationRatio - command.TriggerRatio) <= 0);
                            }

                            if (Math.Abs(Sync.ServerSimulationRatio - command.TriggerRatio) > 0)
                                break;
                            _simSpeedCheck.Add(command, DateTime.Now);
                            break;
                    }
                    break;

                default:
                    throw new Exception("fuck it");
            }
            return false;
        }

        private void TimerElapsed(object sender, ElapsedEventArgs e)
        {
            foreach (var command in EssentialsPlugin.Instance.Config.AutoCommands)
            {
                if (!CanRun(command))
                    continue;

                try
                {
                    command.Update();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error encountered during autocommand update!");
                }
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}
