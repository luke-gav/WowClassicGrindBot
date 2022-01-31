﻿using Core.GOAP;
using SharedLib.NpcFinder;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Numerics;
using System.Linq;

namespace Core.Goals
{
    public class AdhocNPCGoal : GoapGoal, IRouteProvider
    {
        enum PathState
        {
            MoveToPathStart,
            MovePathFile,
            MoveBackToPathStart,
        }

        private PathState pathState;

        private readonly ILogger logger;
        private readonly ConfigurableInput input;

        private readonly AddonReader addonReader;
        private readonly PlayerReader playerReader;
        private readonly IPlayerDirection playerDirection;
        private readonly StopMoving stopMoving;
        private readonly StuckDetector stuckDetector;
        private readonly ClassConfiguration classConfiguration;
        private readonly NpcNameTargeting npcNameTargeting;
        private readonly IBlacklist blacklist;
        private readonly IPPather pather;
        private readonly MountHandler mountHandler;

        private readonly Wait wait;
        private readonly ExecGameCommand execGameCommand;
        private readonly GossipReader gossipReader;
        private readonly KeyAction key;

        private readonly int GossipTimeout = 5000;

        private bool shouldMount;

        private readonly Navigation navigation;

        #region IRouteProvider

        public Stack<Vector3> PathingRoute()
        {
            return navigation.RouteToWaypoint;
        }

        public bool HasNext()
        {
            return navigation.RouteToWaypoint.Count != 0;
        }

        public Vector3 NextPoint()
        {
            return navigation.RouteToWaypoint.Peek();
        }

        public DateTime LastActive { get; set; } = DateTime.Now;

        #endregion

        public AdhocNPCGoal(ILogger logger, ConfigurableInput input, AddonReader addonReader, IPlayerDirection playerDirection, StopMoving stopMoving, NpcNameTargeting npcNameTargeting, StuckDetector stuckDetector, ClassConfiguration classConfiguration, IPPather pather, KeyAction key, IBlacklist blacklist, MountHandler mountHandler, Wait wait, ExecGameCommand exec)
        {
            this.logger = logger;
            this.input = input;
            this.addonReader = addonReader;
            this.playerReader = addonReader.PlayerReader;
            this.playerDirection = playerDirection;
            this.stopMoving = stopMoving;
            this.npcNameTargeting = npcNameTargeting;

            this.stuckDetector = stuckDetector;
            this.classConfiguration = classConfiguration;
            this.pather = pather;
            this.key = key;
            this.blacklist = blacklist;
            this.mountHandler = mountHandler;

            this.wait = wait;
            this.execGameCommand = exec;
            this.gossipReader = addonReader.GossipReader;

            shouldMount = classConfiguration.UseMount;

            navigation = new Navigation(logger, playerDirection, input, addonReader, wait, stopMoving, stuckDetector, pather, mountHandler, key.Path, false);
            navigation.OnDestinationReached += Navigation_OnDestinationReached;
            navigation.OnWayPointReached += Navigation_OnWayPointReached;

            //MinDistance = !(pather is RemotePathingAPIV3) ? MinDistanceMount : 10;

            if (key.InCombat == "false")
            {
                AddPrecondition(GoapKey.incombat, false);
            }
            else if (key.InCombat == "true")
            {
                AddPrecondition(GoapKey.incombat, true);
            }

            this.Keys.Add(key);
        }

        public override float CostOfPerformingAction => key.Cost;

        public override bool CheckIfActionCanRun()
        {
            return key.CanRun();
        }

        public override void OnActionEvent(object sender, ActionEventArgs e)
        {
            if (sender != this)
            {
                shouldMount = true;
                navigation.RouteToWaypoint.Clear();
            }
        }

        public override ValueTask OnEnter()
        {
            SendActionEvent(new ActionEventArgs(GoapKey.fighting, false));

            //TODO: 
            pathState = PathState.MoveToPathStart;


            return ValueTask.CompletedTask;
        }

        public override async ValueTask PerformAction()
        {
            if (this.playerReader.Bits.PlayerInCombat && this.classConfiguration.Mode != Mode.AttendedGather) { return; }

            if (playerReader.Bits.IsDrowning)
            {
                StopDrowning();
            }

            await navigation.Update();

            // should mount
            MountIfRequired();

            LastActive = DateTime.Now;

            wait.Update(1);
        }

        private void Navigation_OnWayPointReached(object? sender, EventArgs e)
        {
            if (pathState is PathState.MovePathFile or PathState.MoveBackToPathStart)
            {
                stopMoving.Stop();
            }
        }

        private async void Navigation_OnDestinationReached(object? sender, EventArgs e)
        {
            // TODO: State machine for different navigation pathing
            // 1 = from any where to Key.Path
            // 2 = follow Key.Path to reach vendor
            // 3 = reverse Key.Path to safe location

            if (pathState == PathState.MoveToPathStart)
            {
                LogWarn("Reached the start point of the path.");
                stopMoving.Stop();

                // we have reached the start of the path to the npc
                navigation.RouteToWaypoint.Clear();

                var path = key.Path.ToList();
                path.Reverse();
                path.ForEach(x => navigation.RouteToWaypoint.Push(x));
                navigation.UpdatedRouteToWayPoint();

                navigation.AllowReduceByDistance = false;
                navigation.PreciseMovement = true;
                stopMoving.Stop();

                pathState++;
            }
            else if (pathState == PathState.MovePathFile)
            {
                LogWarn("Reached defined path end");
                stopMoving.Stop();

                input.TapClearTarget();
                wait.Update(1);

                npcNameTargeting.ChangeNpcType(NpcNames.Friendly | NpcNames.Neutral);
                npcNameTargeting.WaitForNUpdate(1);
                bool foundVendor = npcNameTargeting.FindBy(CursorType.Vendor, CursorType.Repair, CursorType.Innkeeper);
                if (!foundVendor)
                {
                    LogWarn("Not found target by cursor. Attempt to use macro to aquire target");
                    input.KeyPress(key.ConsoleKey, input.defaultKeyPress);
                }

                (bool targetTimeout, double targetElapsedMs) = wait.Until(1000, () => playerReader.HasTarget);
                if (targetTimeout)
                {
                    LogWarn("No target found!");
                    return;
                }

                Log($"Found Target after {targetElapsedMs}ms");

                if (!foundVendor)
                {
                    input.TapInteractKey("Interact with target from macro");
                }

                if (OpenMerchantWindow())
                {
                    input.KeyPress(ConsoleKey.Escape, input.defaultKeyPress);
                    wait.Update(1);

                    input.TapClearTarget();
                    wait.Update(1);

                    navigation.RouteToWaypoint.Clear();

                    var path = key.Path.ToList();
                    path.ForEach(x => navigation.RouteToWaypoint.Push(x));
                    navigation.UpdatedRouteToWayPoint();

                    Log("Look at where i came from!");
                    navigation.LookAtNextWayPoint();

                    wait.Update(1);

                    pathState++;

                    LogWarn("Go back reverse to the start point of the path.");

                    while (HasNext())
                    {
                        await navigation.Update();
                        wait.Update(1);
                    }

                    LogWarn("Reached the start point of the path.");
                }
            }
            else if (pathState == PathState.MoveBackToPathStart)
            {
                pathState = PathState.MoveBackToPathStart;

                navigation.PreciseMovement = false;
                navigation.AllowReduceByDistance = true;
            }
        }


        private void StopDrowning()
        {
            input.TapJump("Drowning! Swim up");
        }

        private void MountIfRequired()
        {
            if (shouldMount && !mountHandler.IsMounted() && !playerReader.Bits.PlayerInCombat)
            {
                shouldMount = false;

                mountHandler.MountUp();

                input.SetKeyState(input.ForwardKey, true, false, "Move forward");
            }
        }

        public override string Name => this.Keys.Count == 0 ? base.Name : this.Keys[0].Name;

        private bool OpenMerchantWindow()
        {
            (bool timeout, double elapsedMs) = wait.Until(GossipTimeout, () => gossipReader.GossipStart || gossipReader.MerchantWindowOpened);
            if (gossipReader.MerchantWindowOpened)
            {
                LogWarn($"Gossip no options! {elapsedMs}ms");
            }
            else
            {
                (bool gossipEndTimeout, double gossipEndElapsedMs) = wait.Until(GossipTimeout, () => gossipReader.GossipEnd);
                if (timeout)
                {
                    LogWarn($"Gossip too many options? {gossipEndElapsedMs}ms");
                    return false;
                }
                else
                {
                    if (gossipReader.Gossips.TryGetValue(Gossip.Vendor, out int orderNum))
                    {
                        Log($"Picked {orderNum}th for {Gossip.Vendor}");
                        execGameCommand.Run($"/run SelectGossipOption({orderNum})--");
                    }
                    else
                    {
                        LogWarn($"Target({playerReader.TargetId}) has no {Gossip.Vendor} option!");
                        return false;
                    }
                }
            }

            Log($"Merchant window opened after {elapsedMs}ms");

            (bool sellStartedTimeout, double sellStartedElapsedMs) = wait.Until(GossipTimeout, () => gossipReader.MerchantWindowSelling);
            if (!sellStartedTimeout)
            {
                Log($"Merchant sell grey items started after {sellStartedElapsedMs}ms");

                (bool sellFinishedTimeout, double sellFinishedElapsedMs) = wait.Until(GossipTimeout, () => gossipReader.MerchantWindowSellingFinished);
                if (!sellFinishedTimeout)
                {
                    Log($"Merchant sell grey items finished, took {sellFinishedElapsedMs}ms");
                    return true;
                }
                else
                {
                    Log($"Merchant sell grey items timeout! Too many items to sell?! Increase {nameof(GossipTimeout)} - {sellFinishedElapsedMs}ms");
                    return true;
                }
            }
            else
            {
                Log($"Merchant sell nothing! {sellStartedElapsedMs}ms");
                return true;
            }
        }

        private void Log(string text)
        {
            logger.LogInformation($"[{nameof(AdhocNPCGoal)}]: {text}");
        }

        private void LogWarn(string text)
        {
            logger.LogWarning($"[{nameof(AdhocNPCGoal)}]: {text}");
        }
    }
}