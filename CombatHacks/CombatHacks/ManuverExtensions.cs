using System;
using System.Collections.Generic;

using UnityEngine;
using System.Reflection;
using PavonisInteractive.TerraInvicta;
using PavonisInteractive.TerraInvicta.Ship;
using PavonisInteractive.TerraInvicta.SpaceCombat;
using PavonisInteractive.TerraInvicta.SpaceCombat.UI;
using System.Reflection.Emit;
using TMPro;

namespace CombatHacks
{

    // Extend the match velocity to after matching velocity, we also match position to an offset from the target ship
    public class ManuverExtensions
    {
        // This should really be stored as part of the WaypointNavCtrl, but as this is a mod, we keep the extra state in a map
        // can clear the map at end/start of combat or remove from it when a ship stops it 
        public static Dictionary<object, ManuverExtensions> extensionMap = new Dictionary<object, ManuverExtensions>();


        // lots of private data we need to extract from the navController; will have a bit of performance impact, but allows us to pretend that we are
        private WaypointNavigationController navController;

        private AdjustableWaypoint[] _waypoints;
        private CombatantController _maneuverTarget;
        private bool _isOutOfCombatDV;
        private TISpaceShipState _shipState;
        private bool _matchVelocityCalculatedThisCycle;
        private Dictionary<AdjustableWaypoint, WaypointController> _waypointControllers;
        public WaypointSharedData _waypointSharedData;

        private FormupManuver formup = null;
        private bool firstTime = true;
        private CombatShipController targetShip;
        private CombatShipController ship;
        private Formation formation;

        ManuverExtensions(WaypointNavigationController navigationController)
        {
            this.navController = navigationController;
            LoadNav();
            ship = FindController(_shipState);
            Debug.Log("Added manuverExtension for " + ship.ShipState.displayName);
        }

        // load the private stuff we need from the navController.
        void LoadNav()
        {
            _waypoints = (AdjustableWaypoint[])LoadField("_waypoints");
            _maneuverTarget = (CombatantController)LoadField("_maneuverTarget");
            _isOutOfCombatDV = (bool)LoadField("_isOutOfCombatDV");
            _shipState = (TISpaceShipState)LoadField("_shipState");
            _matchVelocityCalculatedThisCycle = (bool)LoadField("_matchVelocityCalculatedThisCycle");
            _waypointControllers = (Dictionary<AdjustableWaypoint, WaypointController>)LoadField("_waypointControllers");
            _waypointSharedData = (WaypointSharedData)LoadField("_waypointSharedData");
        }

        // write back anything we need to update ... turns out to only be _matchVelocityCalculatedThisCycle
        private void PushWriteFields()
        {
            StoreField("_matchVelocityCalculatedThisCycle", _matchVelocityCalculatedThisCycle);
        }

        private object LoadField(string fíeldName)
        {
            FieldInfo fieldInfo = typeof(WaypointNavigationController).GetField(fíeldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (fieldInfo == null)
            {
                Debug.Log("No field " + fíeldName + " found!");
            }
            return fieldInfo.GetValue(navController);
        }

        private void StoreField(string fieldName, object value)
        {
            FieldInfo fieldInfo = typeof(WaypointNavigationController).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (fieldInfo == null)
            {
                Debug.Log("No field " + fieldName + " found!");
            }
            fieldInfo.SetValue(navController, value);
        }

        // store stuff back into the navcontroller
        void StoreNav(string v, object value)
        {
            // we do not actually change any fields directly, so no need to save them ...
        }

        void Log(String msg)
        {
            Debug.Log(msg);
        }

        public static String P(Vector3 v)
        {
            return v.ToString("F6") + "/" + v.magnitude.ToString("F4");
        }

        public Vector3 CalcGoalVector(Vector3 offset)
        {
            IWaypoint wp = _waypoints[0];
            Vector3 targetPos = targetShip.positionAtTime(wp.Timing.ExportTime()) - offset;
            return targetPos - wp.Position;
        }


        // the forming up part of the formation manuver.
        // grab the formation, find the leading ship, find the best free slot
        //
        public class FormupManuver
        {
            ManuverExtensions ext;

            Vector3 targetVelocity;
            Vector3 targetHeading;
            Vector3 goalOffset; // offset to goal from target
            Vector3 goalVector; // raw vector to goal
            Vector3 goalVectorN; // normalized vector to goal
            float fineTunedAccTime; // acceleration used
            int accTicks; // ticks needed to accelerate
            int rotationTicks; // ticks needed to rotate
            TIDateTime accStartTime = null; // when we start using ticks
            bool initialized = false;
            public bool failed = false;
            private Vector3 nextValidVelocity;
            private Vector3 nextValidPosition;
            private TIDateTime nextValidTime;

            public FormupManuver(ManuverExtensions ext)
            {
                this.ext = ext;
            }

            private void Init()
            {
                failed = false;
                nextValidTime = null;

                AdjustableWaypoint wp = ext._waypoints[0];

                goalOffset = CalcGoalOffset();

                DateTime time = wp.Timing.ExportTime();

                // Save target heading and velocity; if those changes, we have to restart
                targetVelocity = ext.targetShip.velocityAtTime(time);
                targetHeading = ext.targetShip.headingAtTime(time);

                Vector3 targetShipOffset = ext.targetShip.positionAtTime(time) - wp.Position;

                goalVector = ext.CalcGoalVector(goalOffset);
                goalVectorN = goalVector.normalized;

                // we want to rotate to the targetVector heading, accelerate until midpoint-ish, then turn around and decelerate... we need to drift a tick or two while we rotate
                // as we are constrained to do this in ticks, we are going to select an acceleration that puts us at the correct point when we have decelerated.
                rotationTicks = CalcRotationTicks(Math.PI);
                accTicks = CalcAccTicks(ext._waypointSharedData.LinearAcceleration);
                fineTunedAccTime = CalcFineTunedAccTime();

                // double check ...
                float td = GameControl.spaceCombat.waypointTimeDelta;
                float maxV = fineTunedAccTime * accTicks * td;
                float distUnderAcc = maxV * accTicks * td / 2;
                float distUnderRot = maxV * rotationTicks * td;
                Debug.Log("targetShipOffset " + P(targetShipOffset) + ", goalVector " + P(goalVector) + ", rotTicks " + rotationTicks + ", accTicks " + accTicks + ", fineTune " + fineTunedAccTime + "s, acc " + ext._waypointSharedData.LinearAcceleration);
            }


            /**
             * Calculate the formation slot ... should use some kind of smart way to do this depending; you should select all ships that are going into the formation, then 
             * figure out some smart way of distributing them ... we probably need to store formation data in a static Dict or two somewhere
             */
            private Vector3 CalcGoalOffset()
            {
                Vector3 offset = ext.formation.CalcOffset(ext._shipState);
                return offset;
            }

            // this is where we tune the partical acceleration to allow us to hit the exactly right combination to allow us to exactly reach the formation spot.
            private float CalcFineTunedAccTime()
            {
                // we will acc at maxAcc until the last accTick, where we will accelerate att maxAcc a fraction of the delta and then coast the rest of the way.

                // so we need to adjust the time we accelerate at the last accTick to make the total distance moved be the same as the goal vector.
                float acc = ext._waypointSharedData.LinearAcceleration;
                float td = GameControl.spaceCombat.waypointTimeDelta;

                while (accTicks > 0)
                {

                    float maxAccTicks = accTicks - 1;
                    float tR = rotationTicks * td;

                    // total dist we want to travel 
                    float dist = goalVector.magnitude;
                    // the distance we travel has multiple phases:
                    // 1. max acceleration for maxAccTicks time, velocity v0
                    // 2. next tick starts with a short acc during t1, resulting in speed v1, then drift at v1 speed for the rest of the tick
                    // 3. rotation ticks, drifting at v1 speed
                    // 4. initial deacc to compensate for the acc from 2, then drifting at v0 speed
                    // 5. max deacc for maxAccTicks, after which we have reached speed 0 and our target position

                    // 1 and 5 are symmetrical and can be easily calculated
                    float maxAccDist = acc * maxAccTicks * td * maxAccTicks * td / 2;
                    float v0 = acc * maxAccTicks * td; // base speed after max acc 
                    float remainingDist = dist - maxAccDist * 2;

                    // so steps 2,3,4 needs to fit into remaningDist, with the only parameter being how for how long we accelerate, or t1
                    // so lets calc t1 = x

                    // x2 = x*x
                    // tR = rotationTicks * td; // time spent rotating; normally 1 tick, large ships maybe 2
                    // v1 = v0 + acc * x
                    // v1 * (td - x) = (v0 + acc * x) * (td - x) = v0 * td - v0 * x + acc * x * td - acc * x * x
                    // ... = v0*td + x*(acc * td - v0) - x2 * acc 
                    // ... = -x2(acc) + x(acc * td - v0) + v0*td

                    // 2 =   v0 * x + acc * x2 / 2 + v1 * (td - x) 
                    // ... = x(v0) + x2(acc/2) -x2(acc) + x(acc * td - v0) + v0*td
                    // ... =  x2(acc/2 - acc)   + x(acc * td - v0 + v0)    + v0*td 
                    // *** = -x2(acc/2)         + x(acc * td)              + v0*td 

                    // 3 = v1 * td * rotTicks  = v1 * tR
                    // ... = (v0 + acc * x) * tR
                    // *** =                    + x(acc * tR)              + v0*tR

                    // 4 = v1 * x            - acc * x2 / 2 + v0 * (td - x)
                    // ... = x(v0) + x2(acc) - x2(acc/2) + v0*td - x(v0)
                    // ... = x2(acc - acc/2) + v0 * td
                    // *** = x2(acc/2)                                      + v0*td

                    // So we get a standard quadratic 'a x*x + b x + c = d' formula where
                    // a = -acc/2 + acc/2 = 0 (! ... well, that simplifies things)
                    // b = acc*td + acc*tR = acc(td + tR)
                    // c = v0*td + v0*tR + v0*td = v0(2*td + tR)
                    // d = remainingDist

                    // float a = 0; // nice ... 
                    float b = acc * (td + tR);
                    float c = v0 * (2 * td + tR);
                    float d = remainingDist;

                    // ok, no need to go quadratic, we simply have
                    // bx + c = d -> x = (d - c) / b
                    float t1 = (d - c) / b;


                    if (t1 < 0)
                    {
                        Debug.Log("t1 " + t1 + ", step back accTicks");
                        accTicks--;
                        continue;
                    }
                    if (t1 > td)
                    {
                        // cutting down the accTicks actually cuts down the number of ticks we have to move, so we can end up
                        // having to accelerate harder than we actually can ... increase the number of drift ticks to allow for a smaller acc...
                        Debug.Log("t1 " + t1 + " > td, increse drift time");
                        rotationTicks++;
                        continue;
                    }
                    // Check dV cost; if too much compared to our dv, slow down by adding extra rotationTicks
                    float timeInAcc = 2 * accTicks * td + t1;
                    float dVUsed = timeInAcc * acc;
                    float currentDv = ext._shipState.currentDeltaV_kps * 1000;
                    float maxDvUsage = 0.2f; // should be controllable 
                    if (dVUsed / currentDv > maxDvUsage && rotationTicks < 10)
                    {
                        rotationTicks++;
                        Debug.Log("dvUsage " + (100 * dVUsed / currentDv) + "% > max " + (100 * maxDvUsage) + "%, increasing rt to " + rotationTicks);
                        continue;
                    }

                    // result :
                    // debug ... 
                    float v1 = v0 + t1 * acc;
                    float d2 = v0 * t1 + acc * t1 * t1 / 2 + v1 * (td - t1);
                    float d3 = v1 * td * rotationTicks;
                    float d4 = v1 * t1 - acc * t1 * t1 / 2 + v0 * (td - t1);
                    Debug.Log("### dist " + dist + ", acc " + acc + ", b " + b + ", c " + c + ", d " + d + ", t1 " + t1 + ", v0 " + v0 + ", v1 " + v1 + ", d1 " + maxAccDist + ", d2 " + d2 + ", d3 " + d3 + ", d4 " + d4);

                    return t1;
                }
                // should never get here
                Debug.Log("Failed to calculate a formup path");
                failed = true;
                return 0;
            }

            // how many ticks we need to reach the midpoint under constant acceleration
            private int CalcAccTicks(float acc)
            {
                float len = goalVector.magnitude;
                float rawTimeAcc = (float)Math.Sqrt(len / acc); // time to accelerate to half the distance
                return (int)Math.Ceiling(rawTimeAcc / GameControl.spaceCombat.waypointTimeDelta);
            }

            // how many ticks we need to rotate 180 degrees
            private int CalcRotationTicks(double angle)
            {
                float t = PhysicsHelpers.TimeFromDisplacementAndAcceleration((float)angle * 0.5f, ext._waypointSharedData.AngularAccelerationRads); // spin up, then spin down again .. no spin at waypoints.
                float maxRot = Mathf.Min(ext._waypointSharedData.MaxAngularVelocity, PhysicsHelpers.VelocityFromAccelerationAndTime(ext._waypointSharedData.AngularAccelerationRads, t));
                float rotationTime = (float)(angle / maxRot + maxRot / ext._waypointSharedData.AngularAccelerationRads);
                return (int)Math.Ceiling(rotationTime / GameControl.spaceCombat.waypointTimeDelta); // ticks we need to spend rotating; normally 1, 2+ for really large ships
            }

            // we are valid as long as the heading and velocity of the target ship does not change (or if we have not started yet and we managed to create a path)
            public Boolean IsValid()
            {
                if (!initialized)
                {
                    return true;
                }
                if (failed)
                {
                    return false;
                }
                if (nextValidTime != null)
                {
                    if (nextValidPosition != ext._waypoints[0].PositionAt(nextValidTime))
                    {
                        Debug.Log("invalid position, expected " + nextValidPosition + ", found " + ext._waypoints[0].PositionAt(nextValidTime));
                        return false;
                    }
                    if (nextValidVelocity != ext._waypoints[0].VelocityAt(nextValidTime))
                    {
                        Debug.Log("invalid velocity, expected " + nextValidVelocity + ", found " + ext._waypoints[0].VelocityAt(nextValidTime));
                        return false;
                    }
                }

                DateTime time = ext._waypoints[0].Timing.ExportTime();
                Vector3 vel = ext.targetShip.velocityAtTime(time);
                Vector3 heading = ext.targetShip.headingAtTime(time);
                float dVel = (vel - targetVelocity).magnitude;
                float dHead = (heading - targetHeading).magnitude;
                return dVel < 0.0001 && dHead < 0.0001;
            }

            private String P(Vector3 v)
            {
                return ManuverExtensions.P(v);
            }

            public void Execute()
            {
                if (ext._matchVelocityCalculatedThisCycle)
                {
                    return;
                }
                ext._matchVelocityCalculatedThisCycle = true;


                if (!initialized)
                {
                    Init();
                    initialized = true;
                }

                if (ShouldExit())
                {
                    ext.CleanupAndExit();
                    return;
                }

                Debug.Log("");
                Debug.Log("Formup: start wp calc " + ext._waypoints[0].Timing);

                ext._waypoints[1].ResetCurrentWaypointSequence();
                for (int i = 1; i < ext._waypoints.Length; i++)
                {
                    AdjustableWaypoint prevWp = ext._waypoints[i - 1];
                    AdjustableWaypoint thisWp = ext._waypoints[i];
                    if (thisWp.IsRecursivelyLocked || thisWp.IsInputLocked)
                    {
                        ext.CleanupAndExit();
                        return;
                    }
                    if (ext._waypointControllers[thisWp].IsSystemFailureLocked)
                    {
                        Debug.Log("Unable to calc velocity");
                        ext.CleanupAndExit();
                        return;
                    }

                    CalcNextWaypoint(prevWp, thisWp);
                }
                // detect that we have been tampered with (collision avoidance most likely)
                nextValidTime = ext._waypoints[1].Timing;
                nextValidVelocity = ext._waypoints[1].Velocity;
                nextValidPosition = ext._waypoints[1].Position;
            }

            private bool ShouldExit()
            {
                float td = GameControl.spaceCombat.waypointTimeDelta;
                AdjustableWaypoint wp0 = ext._waypoints[0];
                /*
                Vector3 failVelocity = nextValidVelocity - wp0.Velocity;
                Vector3 failPosition = nextValidPosition - wp0.Position;
                if (failPosition.magnitude > 0.01 || failVelocity.magnitude > 0.01) 
                {
                    Debug.Log("Exit; " + ext._shipState.displayName + ", fail pos " + P(failPosition) + ", fail vel " + P(failVelocity));
                    return true;
                }
                */
                if (accStartTime != null)
                {
                    int tick = (int)Math.Round(ext._waypoints[0].Timing.DifferenceInSeconds(accStartTime) / td);
                    if (tick >= accTicks * 2 + rotationTicks && (wp0.Heading - targetHeading).magnitude < 0.001)
                    {
                        Debug.Log("Finished formup for " + ext._shipState.displayName);
                        return true;
                    }
                }
                return false;
            }

            private void CalcNextWaypoint(AdjustableWaypoint prevWp, AdjustableWaypoint thisWp)
            {
                float td = GameControl.spaceCombat.waypointTimeDelta;
                Vector3 baseDriftOffset = prevWp.Velocity * td;
                Vector3 accOffset = Vector3.zero;
                String step = "";
                int tickIndex = -1;

                float acc = ext._waypointSharedData.LinearAcceleration;

                // Initial phase; accStartTime == 0 because we have not started to accelerate.
                if (accStartTime == null)
                {
                    // Debug.Log("rotate diff " + goalVectorN.Dot(prevWp.Heading) + ", gV " + P(goalVectorN) + ", h " + P(prevWp.Heading));
                    if (goalVectorN.Dot(prevWp.Heading) < 0.99)
                    {
                        step = "IRo";
                        // nope, need to rotate
                        bool hSucc = thisWp.ProposeHeading(goalVectorN);
                        Debug.Log("Rotating from " + P(prevWp.Heading) + " to " + P(goalVectorN) + ", drift " + P(baseDriftOffset) + ", hSucc " + hSucc);
                    }
                    else
                    {
                        // aligned correctly, start blasting!
                        accStartTime = prevWp.Timing;
                        // Debug.Log("accStartTime " + accStartTime);
                    }
                }
                if (accStartTime != null)
                {

                    tickIndex = (int)Math.Round(prevWp.Timing.DifferenceInSeconds(accStartTime) / td);
                    // Debug.Log("tickIndex " + tickIndex + ", i " + i + ", pwpT " + prevWp.Timing);
                    if (tickIndex < accTicks - 1)
                    {
                        // accelerate at max
                        step = "Acc";
                        accOffset = PhysicsHelpers.DisplacementFromAccelerationAndTime(goalVectorN, acc, td);
                        thisWp.ProposePlacement(prevWp.Position + baseDriftOffset + accOffset);
                    }
                    else if (tickIndex < accTicks)
                    {
                        // fine-tuned acc
                        step = "FtA";
                        accOffset = PhysicsHelpers.DisplacementFromAccelerationAndTime(goalVectorN, acc, fineTunedAccTime);
                        Vector3 extraVelAfterAcc = goalVectorN * acc * fineTunedAccTime;
                        Vector3 extraDrift = extraVelAfterAcc * (td - fineTunedAccTime);
                        thisWp.ProposePlacement(prevWp.Position + baseDriftOffset + accOffset + extraDrift);
                    }
                    else if (tickIndex < accTicks + rotationTicks)
                    {
                        // rotate 180 degrees
                        step = "Rot";
                        thisWp.ProposeHeading(goalVectorN * -1);
                        accOffset = Vector3.zero;
                    }
                    else if (tickIndex <= accTicks + rotationTicks)
                    {
                        // fine-tuned deacc
                        step = "FtD";
                        accOffset = PhysicsHelpers.DisplacementFromAccelerationAndTime(goalVectorN, -acc, fineTunedAccTime);
                        Vector3 lostVelAfterAcc = goalVectorN * acc * fineTunedAccTime;
                        Vector3 lostDrift = lostVelAfterAcc * (td - fineTunedAccTime);
                        thisWp.ProposePlacement(prevWp.Position + baseDriftOffset + accOffset - lostDrift);
                    }
                    else if (tickIndex < accTicks * 2 + rotationTicks)
                    {
                        // decelera
                        step = "Dec";
                        accOffset = PhysicsHelpers.DisplacementFromAccelerationAndTime(goalVectorN, -acc, td);
                        thisWp.ProposePlacement(prevWp.Position + baseDriftOffset + accOffset);
                    } else
                    {
                        // final rotation to match heading of leading thisp
                        step = "Frt"; // final rotation
                        thisWp.ProposeHeading(targetHeading);
                    }

                }

                // log the diffs ...
                DateTime t = thisWp.Timing.ExportTime();
                Vector3 targetVelocity = ext.targetShip.velocityAtTime(t);
                Vector3 targetGoalPoint = ext.targetShip.positionAtTime(t) + ext.targetShip.rotation * goalOffset;
                Vector3 currentGoalVector = targetGoalPoint - thisWp.Position;
                float progress = 100 * (goalVector.magnitude - currentGoalVector.magnitude) / goalVector.magnitude;

                float velocityDiff = (prevWp.Velocity - targetVelocity).magnitude;

                // Debug.Log(step + ", i " + tickIndex + ", prog " + progress + "%, drift " + baseDriftOffset.magnitude + ", dAcc " + accOffset.magnitude + ", velDiff " + velocityDiff + ", posDiff " + P(thisWp.Position - targetGoalPoint));
            }
        }

        internal IWaypoint GetWayPoint0()
        {
            return _waypoints[0];
        }

        public CombatShipController FindController(TISpaceShipState shipState)
        {
            Debug.Log("Find ctrl for " + shipState.displayName + ", " + GameControl.spaceCombat.ships.Count + " ships to choose from");
            foreach (var ship in GameControl.spaceCombat.ships)
            {
                Debug.Log("ship " + ship.ref_shipController.ShipState.displayName);
                if (ship.ref_shipController.ShipState == shipState)
                {
                    Debug.Log("ship " + ship.ref_shipController.ShipState.displayName + " matches");
                    return ship;
                }
            }
            Debug.Log("No ship found");
            return null;
        }

        public void MatchVelocityAndPos()
        {
            if (this._matchVelocityCalculatedThisCycle)
            {
                return;
            }

            TIDateTime now = _waypoints[0].Timing;

            if (formation != null)
            {
                if (!formation.IsValid(now))
                {
                    CleanupAndExit();
                    return;
                }
                formation.AssignSlots(now);
            }


            if (this._maneuverTarget == null)
            {
                Debug.Log("Exit MatchVelocityAndPos for " + this._shipState.displayName + ", no target");
                CleanupAndExit();
                return;
            }

            if (firstTime)
            {
                firstTime = false;
                bool pDown = Input.GetKey(KeyCode.P);
                bool ctrlDown = Input.GetKey(KeyCode.LeftControl);
                Debug.Log("firstTime for " + ship.ShipState.displayName + ", pDown " + pDown + ", ctrlDown " + ctrlDown);
                CombatShipController selectedShip = _maneuverTarget.ref_shipController;
                formation = Formation.FindFormation(selectedShip.ShipState);
                if (formation != null && (!formation.IsValid(now) && !formation.Selectable(selectedShip)))
                {
                    formation.Destroy();
                    formation = null;
                }
                if (formation == null)
                {
                    // create the formation
                    formation = Formation.GetFormation(selectedShip, now);
                    Debug.Log("Created formation " + formation.name);
                }
                else
                {
                    Debug.Log("Found formation " + formation.name);
                }
                formation.AddShip(ship);
                targetShip = FindController(formation.leadingShip.ShipState);
            }

            if (!formation.IsValid(now))
            {
                // the formation we are part of is no longer valid, abort
                Debug.Log("Formation " + formation.name + ", no longer valid");
                formation.Destroy();
                CleanupAndExit();
                return;
            }

            // this allows us to select any ship in the formation instead of keeping track of the leading ship
            if (targetShip == null || targetShip.destructionTriggered || targetShip.ShipState == this._shipState || this._isOutOfCombatDV)
            {
                Debug.Log("Exit MatchVelocityAndPos for " + this._shipState.displayName + "(targetShip invalid)");
                CleanupAndExit();
                return;
            }

            // check what phase we are in; either matching velocity or forming up
            // when we start forming up, we save the position of our ship, the target ship pos and velocity, and the target point relative to our position
            // at this time, we recalc the target point, checks how much the target ship has moved, adds that to the start position. If they match, we are in the formup phase.
            bool velocityMatched = false;
            if (this.formup == null)
            {
                velocityMatched = MatchVelocity();
            }
            if (this.formup == null && velocityMatched && formation.IsReady())
            {
                this.formup = new FormupManuver(this);
            }
            if (this.formup != null)
            {
                if (formup.IsValid())
                {
                    this.formup.Execute();
                }
                else
                {
                    // could just do a formup = null to restart the process, but usually this is triggered by leading ship deciding that it needs to do collision avoidance
                    // which causes all kinds of fecall matter to hit the rotating device. So better to just abort ... 
                    CleanupAndExit();
                }
            }
        }

        private void CleanupAndExit()
        {
            Debug.Log("Exit " + ship.ShipState.displayName);
            this._shipState.RemoveCombatManeuver(CombatManeuver.MatchVelocity);
            this._matchVelocityCalculatedThisCycle = false;
            navController.MatchVelocityEnabled = false;
            formup = null;
            firstTime = true;
            formation.Exit(ship);
            formation = null;
           
        }

        private static float RotationTime(CombatShipController ship, Vector3 forward, Quaternion rotation)
        {
            float angAcc = ship.angular_acceleration_rads2;
            float maxAngVel = ship.max_angular_velocity_rads_s;

            Quaternion desiredRotation = Quaternion.LookRotation(forward);
            float rotationTime = 0f;
            float requiredRotation = Math.Abs(PhysicsHelpers.RadianAngleBetweenVectors(rotation * Vector3.forward, desiredRotation * Vector3.forward));
            if (requiredRotation > 1E-45f)
            {
                rotationTime = WaypointTrajectorySequence.TimeRequiredForHeadingRotation(rotation, desiredRotation, angAcc, maxAngVel);
            }
            return rotationTime;
        }

        // Returns the index of the last waypoint calculated
        public bool CalcVelocityMatch(CombatShipController ship, Vector3 targetVelocity, AdjustableWaypoint[] waypoints)
        {
            float acc = this._waypointSharedData.LinearAcceleration;
            float td = GameControl.spaceCombat.waypointTimeDelta;

            Debug.Log("CalcVelMatch " + ship.ShipState.displayName + ", acc " + acc + ", max dv change " + (acc * td));
            for (int i = 1; i < waypoints.Length; i++)
            {
                AdjustableWaypoint thisWp = waypoints[i];
                if (thisWp.IsRecursivelyLocked || thisWp.IsInputLocked)
                {
                    continue;
                }
                // TODO: need to add in that system-locked stanza again
                AdjustableWaypoint prevWp = waypoints[i - 1];
                Vector3 diffVector = (targetVelocity - prevWp.VelocityAt(prevWp.Timing));
                Debug.Log("diffVector " + P(diffVector));
                // we are finished if we are inside .1% of target
                if (diffVector.magnitude < 0.001)
                {
                    return i == 1; // if this is the first waypoint, we are done
                }

                float requiredBurnTime = diffVector.magnitude / acc; // theoretical burn .. 
                float rotationTime = Math.Min(td, RotationTime(ship, diffVector, prevWp.Rotation)); // need to rotate to the right heading first
                float burnTime = Math.Max(0, Math.Min(requiredBurnTime, td - rotationTime)); // effective burn time, limited by td and rotationTime
                float driftTime = td - rotationTime - burnTime; // anything left over after rotation and burn

                Vector3 diffHeading = diffVector.normalized;

                Vector3 distInTurning = prevWp.Velocity * rotationTime;
                // once we have finished rotating, we accelerate until diffVelocity is zero
                Vector3 distInAcc = prevWp.Velocity * burnTime + diffHeading * PhysicsHelpers.DisplacementFromAccelerationAndTime(acc, burnTime);
                // you can only drift once targetVelocity is attained
                Vector3 distDrift = targetVelocity * driftTime;

                Debug.Log("Time to burn " + burnTime + ", req " + requiredBurnTime + ", time to rotate " + rotationTime + ", time to drift " + driftTime + ", waypointDeltaTime " + td);
                Vector3 position = prevWp.Position + distInTurning + distInAcc + distDrift;
                thisWp.ProposePlacement(position);
            }
            return false;
        }

        public bool MatchVelocity()
        {
            this._matchVelocityCalculatedThisCycle = true;
            this._waypoints[1].ResetCurrentWaypointSequence();
            Vector3 targetVelocity = (this._maneuverTarget.ref_shipController != null) ? this._maneuverTarget.ref_shipController.velocityAtTime(this._waypoints[0].Timing.ExportTime()) : this._maneuverTarget.ref_habModuleController.velocityVector;
            return CalcVelocityMatch(ship, targetVelocity, this._waypoints);
        }


        internal static void RunMatchVelocity(WaypointNavigationController controller)
        {
            if (!extensionMap.ContainsKey(controller))
            {
                // TODO: needs cleaning up on combat exit ... need to find event for it I guess?
                ManuverExtensions m = new ManuverExtensions(controller);
                extensionMap[controller] = m;
                extensionMap[m.ship.ShipState] = m;
            }
            ManuverExtensions ext = extensionMap[controller];
            ext.LoadNav();
            ext.MatchVelocityAndPos();
            ext.PushWriteFields();
        }

        public static ManuverExtensions GetManuverExtensions(TISpaceShipState shipState)
        {
            if (extensionMap.ContainsKey(shipState))
            {
                return extensionMap[shipState];
            }
            return null;
        }
    }
}
