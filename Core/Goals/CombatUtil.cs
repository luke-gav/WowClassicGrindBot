﻿using Microsoft.Extensions.Logging;
using SharedLib.Extensions;
using System.Numerics;

namespace Core
{
    public class CombatUtil
    {
        private readonly ILogger logger;
        private readonly AddonReader addonReader;
        private readonly PlayerReader playerReader;
        private readonly ConfigurableInput input;
        private readonly Wait wait;

        private const bool debug = true;

        private bool outOfCombat;
        private Vector3 lastPosition;

        public CombatUtil(ILogger logger, ConfigurableInput input, Wait wait, AddonReader addonReader)
        {
            this.logger = logger;
            this.input = input;
            this.wait = wait;
            this.addonReader = addonReader;
            this.playerReader = addonReader.PlayerReader;

            outOfCombat = !playerReader.Bits.PlayerInCombat;
            lastPosition = playerReader.PlayerLocation;
        }

        public void Update()
        {
            // TODO: have to find a better way to reset outOfCombat
            outOfCombat = !playerReader.Bits.PlayerInCombat;
            lastPosition = playerReader.PlayerLocation;
        }

        public bool EnteredCombat()
        {
            wait.Update();
            if (!outOfCombat && !playerReader.Bits.PlayerInCombat)
            {
                Log("Combat Leave");
                outOfCombat = true;
                return false;
            }

            if (outOfCombat && playerReader.Bits.PlayerInCombat)
            {
                Log("Combat Enter");
                outOfCombat = false;
                return true;
            }

            return false;
        }

        public bool AquiredTarget()
        {
            if (this.playerReader.Bits.PlayerInCombat)
            {
                if (this.playerReader.PetHasTarget)
                {
                    input.TargetPet();
                    Log($"Pets target {playerReader.TargetTarget}");
                    if (playerReader.TargetTarget == TargetTargetEnum.PetHasATarget)
                    {
                        Log($"{nameof(AquiredTarget)}: Found target by pet");
                        input.TargetOfTarget();
                        return true;
                    }
                }

                input.NearestTarget();
                wait.Update();

                if (playerReader.HasTarget &&
                    playerReader.Bits.TargetInCombat &&
                    (playerReader.Bits.TargetOfTargetIsPlayerOrPet ||
                    addonReader.CreatureHistory.DamageDone.Exists(x => x.Guid == playerReader.TargetGuid)))
                {
                    Log("Found target");
                    return true;
                }

                if (wait.Till(400, () => playerReader.HasTarget || playerReader.PetHasTarget))
                {
                    return true;
                }

                Log($"{nameof(AquiredTarget)}: No target found");
                input.ClearTarget();
                wait.Update();
            }
            return false;
        }

        public bool IsPlayerMoving(Vector3 lastPos)
        {
            var distance = playerReader.PlayerLocation.DistanceXYTo(lastPos);
            return distance > 0.01f;
        }

        public (bool foundTarget, bool hadToMove) FoundTargetWhileMoved()
        {
            (bool movedTimeOut, double elapsedMs) = wait.Until(200, () => lastPosition != playerReader.PlayerLocation);
            if (!movedTimeOut)
            {
                Log($"  Went for corpse {elapsedMs}ms");
            }

            while (IsPlayerMoving(lastPosition))
            {
                lastPosition = playerReader.PlayerLocation;
                if (!wait.Till(100, EnteredCombat))
                {
                    if (AquiredTarget())
                        return (true, !movedTimeOut);
                }
                // due limited precision
                wait.Update();
            }

            return (false, !movedTimeOut);
        }

        private void Log(string text)
        {
            if (debug)
            {
                logger.LogDebug($"{nameof(CombatUtil)}: {text}");
            }
        }
    }
}
