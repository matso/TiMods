using System;
using System.Collections.Generic;
using System.Linq;

using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;
using System.Reflection;
using PavonisInteractive.TerraInvicta;
using PavonisInteractive.TerraInvicta.Ship;
using PavonisInteractive.TerraInvicta.SpaceCombat;
using PavonisInteractive.TerraInvicta.SpaceCombat.UI;
using System.Reflection.Emit;
using TMPro;


// control salvo size; press CTRL when firing a salvo and the salvo size will be just 1 missile.
namespace CombatHacks
{
    public class Hacks
    {
        public static bool enabled;
        public static UnityModManager.ModEntry mod;
        private static bool patched = false;
        private static Harmony harmony;

        //This is standard code, you can just copy it directly into your mod
        static bool Load(UnityModManager.ModEntry modEntry)
        {
            Debug.Log("Loading CombatHacks, enabled " + enabled);
            harmony = new Harmony(modEntry.Info.Id);
            mod = modEntry;
            modEntry.OnToggle = OnToggle;
            CheckPatch();
            return true;
        }

        private static void CheckPatch()
        {
            if (enabled && !patched)
            {
                Debug.Log("Patching CombatHacks");
                harmony.PatchAll();
                patched = true;
            }
        }

        static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            enabled = value;
            Debug.Log("OnToggle CombatHacks, enabled " + enabled);
            CheckPatch();
            return true;
        }

        /*
        [HarmonyPatch(typeof(WaypointTrajectorySequence), nameof(WaypointTrajectorySequence.CreateConstrainedTrajectory))]
        static class CreateConstrainedTrajectoryDebug
        {
            public static bool Prefix(WaypointTrajectorySequence __instance, IWaypoint start, IProposedWaypoint target, AccelerationConstraints constraints, bool preserveRoll)
            {
                if (Hacks.enabled)
                {
                    // analyse stuff ...
                    Debug.Log("CCTDebug: start " + start + ", target " + target + ", cons " + constraints + ", roll " + preserveRoll);
                    float num = (float)(target.Timing - start.Timing).TotalSeconds;
                    Vector3 vector = PhysicsHelpers.PositionFromVelocityAndTime(start.Position, start.Velocity, num);
                    Vector3 desiredDisplacementVector = target.Position - vector;
                    

                    float num2 = target.RotationAllowed ? WaypointTrajectorySequence.TimeRequiredForHeadingRotation(start.Rotation, target.Rotation, constraints.AngularAcceleration, constraints.MaxAngularVelocity) : WaypointTrajectorySequence.TimeRequiredForDrift(desiredDisplacementVector, constraints.LinearAcceleration);
                    if (num2 <= num || num2 == 3.4028235E+38f)
                    {
                        if (num2 == 3.4028235E+38f)
                        {
                            target.Heading = start.Heading;
                        }
                        float magnitude = desiredDisplacementVector.magnitude;
                        float num3 = PhysicsHelpers.DisplacementFromAccelerationAndTime(constraints.LinearAcceleration, num);
                        if (!WaypointTrajectorySequence.IsTargetReachable(magnitude, num3))
                        {
                            if (target.IsPositionLocked)
                            {
                                return WaypointTrajectorySequence.InvalidTrajectorySequence;
                            }
                            target.Position = vector + desiredDisplacementVector.normalized * num3;
                        }
                        if (magnitude > 0.01f && constraints.AngularAcceleration > 1E-45f)
                        {
                            target.SetHeading(desiredDisplacementVector.normalized, preserveRoll);
                        }
                        return new WaypointTrajectorySequence(start, target, magnitude, constraints);
                    }
                    if (!target.IsPositionLocked)
                    {
                        target.Position = vector;
                        WaypointTrajectorySequence waypointTrajectorySequence = new WaypointTrajectorySequence(start, target, constraints, num);
                        waypointTrajectorySequence.AdjustEndHeading(target, constraints);
                        return waypointTrajectorySequence;
                    }
                    return WaypointTrajectorySequence.InvalidTrajectorySequence;
                    
                }
                return true;
            }

        }
*/


        [HarmonyPatch(typeof(SalvoFireMode), "OnWeaponModeChanged")]
        [HarmonyPatch(new Type[] { typeof(ShipWeaponModeChanged) })]
        static class SalvoSizePatch
        {
            static void Postfix(SalvoFireMode __instance, ShipWeaponModeChanged e)
            {
                if (Hacks.enabled)
                {
                    if (Input.GetKey(KeyCode.LeftControl))
                    {
                        // Cut salvos size to 1 by setting already fired shots to salvoSize -1; 
                        var salvosize = (int)__instance.GetType().GetField("_totalSalvo", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);
                        var shotsFireRef = __instance.GetType().GetField("_shotsFired", BindingFlags.NonPublic | BindingFlags.Instance);
                        int shotsFired = (int)shotsFireRef.GetValue(__instance);
                        int desired = salvosize - 1;
                        if (shotsFired != desired)
                        {
                            shotsFireRef.SetValue(__instance, desired);
                            // Debug.Log("Setting shots fired to " + desired);
                        }
                    }

                }
            }
        }

        // fragile as hell ... replaces the assignment of two local variables with the call to the resort methods
        [HarmonyPatch(typeof(SpaceCombatCanvasController), "SetupShipLists")]
        [HarmonyPatch(new Type[] { typeof(List<CombatShipController>) })]
        static class SortShipList
        {
            internal static CombatShipController enemyShip;
            internal static CombatShipController friendlyShip;
            internal static SpaceCombatCanvasController canvasController;
            private static List<CombatShipController> rightHandOrdering;
            private static List<CombatShipController> leftHandOrdering;

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {

                if (Hacks.enabled)
                {
                    Debug.Log("... patching SetupShipLists");
                    MethodInfo addRangeMethodInfo = typeof(List<CombatantController>).GetMethod("AddRange");
                    MethodInfo resortLeftMethodInfo = typeof(SortShipList).GetMethod("ResortLeft");
                    MethodInfo resortRightMethodInfo = typeof(SortShipList).GetMethod("ResortRight");

                    List<CodeInstruction> instructionList = instructions.ToList();
                    List<CodeInstruction> buffer = new List<CodeInstruction>();
                    List<CodeInstruction> result = new List<CodeInstruction>();
                    Boolean leftResort = false;
                    Boolean leftDone = false;
                    Boolean rightDone = false;

                    for (int i = 0; i < instructionList.Count; i++)
                    {
                        CodeInstruction instruction = instructionList[i];
                        if (instruction.Calls(addRangeMethodInfo))
                        {
                            // Debug.Log("detects AddRange call at " + i + ", bufSize " + buffer.Count());
                            if (buffer.Count() == 4)
                            {
                                buffer.RemoveAt(buffer.Count() - 1); // remove the load of activeShipControllers; want to be called with the containing object as param
                                result.AddRange(buffer);
                                buffer.Clear();
                                if (leftResort)
                                {
                                    result.Add(new CodeInstruction(OpCodes.Call, resortLeftMethodInfo));
                                    Debug.Log("... patching left AddRange call at " + i);
                                    leftDone = true;
                                }
                                else
                                {
                                    result.Add(new CodeInstruction(OpCodes.Call, resortRightMethodInfo));
                                    Debug.Log("... patching right AddRange call at " + i);
                                    rightDone = true;
                                }
                                continue; // skip the call to addRange
                            }
                        }
                        if (buffer.Count() > 5)
                        {
                            //Debug.Log("giving up on AddRange call at " + i);
                            result.AddRange(buffer);
                            buffer.Clear();
                        }
                        if (buffer.Count() > 0)
                        {
                            // Debug.Log("waiting for AddRange call at " + i + ", instr " + instruction);
                            buffer.Add(instruction);
                            continue;
                        }
                        if (instruction.opcode == OpCodes.Ldloc_2 && !leftDone)
                        {
                            // Debug.Log("start wait for left AddRange call at " + i);
                            buffer.Add(instruction);
                            leftResort = true;
                        }
                        if (instruction.opcode.Value == 0x11 && !rightDone)
                        {
                            // Debug.Log("start wait for right AddRange call at " + i + ", " + instruction + ", op " + instruction.operand);
                            buffer.Add(instruction);
                            leftResort = false;
                        }
                        if (buffer.Count() == 0)
                        {
                            result.Add(instruction);
                        }
                    }
                    result.AddRange(buffer);

                    int j = 0;
                    for (int i = 0; i < instructionList.Count(); i++)
                    {
                        if (!result[j].Equals(instructionList[i]))
                        {
                            // Debug.Log("diffs: " + i + ": " + instructionList[i] + " != " + result[j]);
                            i++;
                        }
                        j++;
                    }
                    return result.AsEnumerable();
                }
                return instructions;
            }
            public static void ResortLeft(List<CombatantController> list, CombatFleetController controller)
            {

                // This replaces the list2.AddRange(this.leftHandFleetController.activeShipControllers);
                // we first resort the activeShipControllers, then do the AddRange to list before returning
                DoResort(list, controller, leftHandOrdering);
            }
            public static void ResortRight(List<CombatantController> list, CombatFleetController controller)
            {
                DoResort(list, controller, rightHandOrdering);
            }

            private static void DoResort(List<CombatantController> list, CombatFleetController controller, List<CombatShipController> orderedList)
            {
                if (orderedList != null)
                {
                    // Debug.Log("Using custom ordering");
                    controller.activeShipControllers = new List<CombatShipController>(orderedList);
                }
                list.AddRange(controller.activeShipControllers);
            }

            internal static void checkForResort(bool forceReorder)
            {
                if (canvasController == null) return;

                Boolean update = forceReorder;
                if (friendlyShip != null)
                {
                    IList<CombatShipController> enemies = canvasController.rightHandFleetController.activeShipControllers;
                    var enemyDistances = CalcDistances(friendlyShip.position, enemies);
                    rightHandOrdering = (from e in enemyDistances orderby e.Value select e.Key).ToList();
                    update = update || !rightHandOrdering.Equals(enemies);
                }

                if (enemyShip != null)
                {
                    IList<CombatShipController> friendlies = canvasController.leftHandFleetController.activeShipControllers;
                    var friendlyDistances = CalcDistances(enemyShip.position, friendlies);
                    leftHandOrdering = (from e in friendlyDistances orderby e.Value select e.Key).ToList();
                    update = update || !leftHandOrdering.Equals(friendlies);
                    // Debug.Log("sort friendly, enemyShip " + enemyShip + ", " + Stringify(friendlyDistances) + " -> " + Stringify(leftHandOrdering));
                }
                if (update)
                {
                    // Debug.Log("resort...");
                    // the method we need is private, so ...
                    UpdateShipLists();
                }
            }

            private static string Stringify(Dictionary<CombatShipController, double> dict)
            {
                return "{" + String.Join(", ", dict.Select(e => e.Key + ":" + e.Value)) + "}";
            }

            private static string Stringify(IEnumerable<dynamic> iter)
            {
                return "[" + String.Join(", ", iter.Select(i => "" + i)) + "]";
            }

            private static void UpdateShipLists()
            {
                MethodInfo methodInfo = typeof(SpaceCombatCanvasController).GetMethod("UpdateCombatShipList", BindingFlags.NonPublic | BindingFlags.Instance);
                // Debug.Log("method " + methodInfo);
                methodInfo.Invoke(canvasController, new object[] { null });
            }

            internal static Dictionary<CombatShipController, double> CalcDistances(Vector3 pos, IList<CombatShipController> ships)
            {
                var result = new Dictionary<CombatShipController, double>();
                foreach (var ship in ships)
                {
                    double dist = Vector3.Distance(pos, ship.position);
                    result.Add(ship, dist);
                }
                return result;
            }

            internal static void SelectEnemyShip(CombatShipController ship)
            {
                enemyShip = ship;
                checkForResort(true);
            }
            internal static void SelectFriendlyShip(CombatShipController ship)
            {
                friendlyShip = ship;
                checkForResort(true);
            }

            internal static void Clear()
            {
                enemyShip = null;
                friendlyShip = null;
                canvasController = null;
                rightHandOrdering = null;
                leftHandOrdering = null;
            }
        }

        public class TargetListener
        {
            private SpaceCombatCanvasController canvasController;

            public TargetListener(SpaceCombatCanvasController controller)
            {
                this.canvasController = controller;
                SortShipList.canvasController = controller;
            }
            public void OnCombatTargetableStateSelected(CombatTargetedableStateSelected e)
            {
                // how we select enemy ships 
                if (e.target != null)
                {
                    TISpaceShipState tispaceShipState = e.target.GetTargetableState() as TISpaceShipState;
                    if (((tispaceShipState != null) ? tispaceShipState.faction : null) != canvasController.activePlayer)
                    {
                        CombatShipController target = (CombatShipController)canvasController.combatMgr.combatantLookup[tispaceShipState];
                        if (target != SortShipList.enemyShip)
                        {
                            Debug.Log("Selected friendly ship " + target);
                            SortShipList.SelectEnemyShip(target);
                        }
                    }
                }

            }

            public void OnCombatSecond(CombatSecond e)
            {
                // Debug.Log("CombatSecond " + e);
                SortShipList.checkForResort(false);
            }
        }
        [HarmonyPatch(typeof(SpaceCombatCanvasController), "SelectPrimaryShip")]
        [HarmonyPatch(new Type[] { typeof(CombatShipController) })]
        static class OnSelectPrimaryShip
        {
            public static void Postfix(SpaceCombatCanvasController __instance, CombatShipController shipController)
            {
                if (shipController != SortShipList.friendlyShip)
                {
                    // Debug.Log("Selected friendly ship " + shipController);
                    SortShipList.SelectFriendlyShip(shipController);
                }
            }

            [HarmonyPatch(typeof(SpaceCombatCanvasController), "Show")]
            static class OnShow
            {
                public static TargetListener listener;
                public static void Postfix(SpaceCombatCanvasController __instance)
                {
                    if (Hacks.enabled)
                    {
                        SortShipList.Clear();
                        listener = new TargetListener(__instance);
                        GameControl.eventManager.AddListener<CombatTargetedableStateSelected>(new EventManager.EventDelegate<CombatTargetedableStateSelected>(listener.OnCombatTargetableStateSelected), null, null, false, false);
                        GameControl.eventManager.AddListener<CombatSecond>(new EventManager.EventDelegate<CombatSecond>(listener.OnCombatSecond), null, null, true, false);
                    }
                }

            }
            [HarmonyPatch(typeof(SpaceCombatCanvasController), "Hide")]
            static class OnHide
            {
                public static void Postfix(SpaceCombatCanvasController __instance)
                {
                    if (Hacks.enabled)
                    {
                        SortShipList.Clear();
                        if (OnShow.listener != null)
                        {
                            GameControl.eventManager.RemoveListener<CombatTargetedableStateSelected>(new EventManager.EventDelegate<CombatTargetedableStateSelected>(OnShow.listener.OnCombatTargetableStateSelected), null);
                            GameControl.eventManager.RemoveListener<CombatSecond>(new EventManager.EventDelegate<CombatSecond>(OnShow.listener.OnCombatSecond), null);
                            OnShow.listener = null;
                        }
                    }
                }
            }

        }

        // add number of missiles targeting me to the enemy ship display
        // use the same display as the range text display

        static Dictionary<CombatantController, int> missileCache = null;
        static double missileCacheTime = 0;

        [HarmonyPatch(typeof(EnemyShipListItemController), "UpdateTargetDistance")]
        static class UpdateTargetDistance_MissileCount
        {
            public static void Postfix(EnemyShipListItemController __instance, CombatSecond e)
            {
                if (Hacks.enabled)
                {
                    SpaceCombatCanvasController masterController = GetMasterController(__instance);

                    if (__instance.combatantController == null)
                    {
                        return;
                    }
                    UpdateText(__instance, masterController);
                }
            }
        }

        [HarmonyPatch(typeof(EnemyShipListItemController), "ShowTargetDistance")]
        static class ShowTargetDistance_MissileCount
        {
            public static void Postfix(EnemyShipListItemController __instance)
            {
                if (Hacks.enabled)
                {
                    SpaceCombatCanvasController masterController = GetMasterController(__instance);

                    if (__instance.combatantController == null)
                    {
                        return;
                    }
                    UpdateText(__instance, masterController);
                }
            }
        }

        public static void UpdateText(EnemyShipListItemController __instance, SpaceCombatCanvasController masterController)
        {
            TMP_Text txtField = __instance.distanceToTargetTxt;

            string distTxt = "";
            if (masterController.selectedFriendlyShip != null)
            {
                float dist = Vector3.Distance(__instance.combatantController.position, masterController.selectedFriendlyShip.position);
                distTxt = Loc.T("UI.Space.Distkm", new object[] { SpaceCombatManager.scale_to_km(dist).ToString("F0") });
            }
            int numMissiles = GetMissilesTargetedAt(__instance.combatantController, masterController);
            string missileText = numMissiles == 0 ? "" : " / m: " + numMissiles;

            txtField.SetText(distTxt + missileText);
        }

        private static int GetMissilesTargetedAt(CombatantController ship, SpaceCombatCanvasController masterController)
        {
            double time = GameControl.spaceCombat.combatDuration_s;
            if (missileCache == null || missileCacheTime != time)
            {
                missileCache = UpdateMissileCache(masterController);
                missileCacheTime = time;
            }
            if (missileCache.ContainsKey(ship))
            {
                return missileCache[ship];
            }
            return 0;
        }

        private static Dictionary<CombatantController, int> UpdateMissileCache(SpaceCombatCanvasController masterController)
        {
            var result = new Dictionary<CombatantController, int>();
            foreach (ProjectileController projectileController in GameControl.spaceCombat._projectiles.Values)
            {
                if (!(projectileController == null) && projectileController.isMissile && !projectileController.hasHit && !projectileController.beenDestroyed)
                {
                    MissileController missileController = projectileController as MissileController;
                    var target = missileController.target as CombatantController;
                    if (target != null)
                    {
                        if (TIUtilities.MovingTowardsTarget(target.position, target.velocityVector, missileController.position, missileController.velocityVector))
                        {
                            result.TryGetValue(target, out var currentCount);
                            result[target] = currentCount + 1;
                        }
                    }
                }
            }
            return result;
        }

        private static SpaceCombatCanvasController GetMasterController(EnemyShipListItemController controller)
        {
            return (SpaceCombatCanvasController)typeof(EnemyShipListItemController).GetField("masterController", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(controller);
        }

        // add number of missiles targeting me to the enemy ship display
        // use the same display as the range text display

        [HarmonyPatch(typeof(WaypointNavigationController), "MatchVelocity")]
        static class PatchMatchVelocity
        {
            public static bool Prefix(WaypointNavigationController __instance)
            {
                if (Hacks.enabled)
                {
                    ManuverExtensions.RunMatchVelocity(__instance);
                    return false; // skip original
                }
                return true; // run the original
            }
        }



        static void Main(string[] args)
        {
            Console.WriteLine("Hello");
        }
    }

}
