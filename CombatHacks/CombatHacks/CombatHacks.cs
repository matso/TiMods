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
using PavonisInteractive.TerraInvicta.Systems.GameTime;
using Unity.Entities;


// control salvo size; press CTRL when firing a salvo and the salvo size will be just 1 missile.
namespace CombatHacks
{
    public class Hacks
    {
        public static bool enabled;
        public static UnityModManager.ModEntry mod;
        private static bool patched = false;
        private static Harmony harmony;
        private static GameTimeManager gameTime;

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
                gameTime = World.Active.GetExistingManager<GameTimeManager>();
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
                    if (e.ship.faction == SortShipList.canvasController.activePlayer && TIInputManager.IsAltKeyDown)
                    {
                        // Cut salvos size to 1 by setting already fired shots to salvoSize -1; 
                        var salvosize = (int)__instance.GetType().GetField("_totalSalvo", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);
                        var shotsFireRef = __instance.GetType().GetField("_shotsFired", BindingFlags.NonPublic | BindingFlags.Instance);
                        int shotsFired = (int)shotsFireRef.GetValue(__instance);
                        if (shotsFired != salvosize)
                        {
                            // set shots fired to salvosize -1, allowing 1 more missile to be fired.
                            Debug.Log("Salvo 1 missile");
                            shotsFireRef.SetValue(__instance, salvosize - 1);
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
            internal static CombatShipController enemyCandidate;
            internal static CombatShipController friendlyShip;
            internal static CombatShipController friendlyCandidate;
            public static SpaceCombatCanvasController canvasController;
            private static List<CombatShipController> rightHandOrdering;
            private static List<CombatShipController> leftHandOrdering;
            private static int lastEnemyShipSelectTime;
            private static int lastFriendlyShipSelectTime;

            public static void Postfix(SpaceCombatCanvasController __instance, List<CombatShipController> shipsToHighlight)
            {
                if (enemyShip != null)
                {
                    IList<CombatShipController> friendlies = canvasController.leftHandFleetController.activeShipControllers;
                    var friendlyDistances = CalcDistances(enemyShip.position, friendlies);
                    leftHandOrdering = (from e in friendlyDistances orderby e.Value select e.Key).ToList();

                    Debug.Log("Resort friendly, lHO " + leftHandOrdering.Count + ", " + __instance.friendlyShipList.size);
                    ResortShipList(__instance, shipsToHighlight, __instance.friendlyShipList, leftHandOrdering);
                }
                if (friendlyShip != null)
                {
                    IList<CombatShipController> enemies = canvasController.rightHandFleetController.activeShipControllers;
                    var enemyDistances = CalcDistances(friendlyShip.position, enemies);
                    rightHandOrdering = (from e in enemyDistances orderby e.Value select e.Key).ToList();

                    Debug.Log("Resort enemy, rHO " + rightHandOrdering.Count + ", " + __instance.enemyShipList.size);
                    ResortShipList(__instance, shipsToHighlight, __instance.enemyShipList, rightHandOrdering);
                }
            }

            private static void ResortShipList(SpaceCombatCanvasController __instance, List<CombatShipController> shipsToHighlight, ListManagerBase shipList, List<CombatShipController> sortedShips)
            {
                int index = 0;
                using (IEnumerator<object> enumerator = shipList.GetEnumerator())
                {
                    while (enumerator.MoveNext())
                    {
                        CombatantController cc = sortedShips[index];

                        CombatantListItemController shipListItemController = (CombatantListItemController)enumerator.Current;
                        shipListItemController.Init(__instance, cc, index);
                        cc.UIController().InitializeForCombat(cc, shipListItemController);
                        if (!cc.isDestroyed)
                        {
                            shipListItemController.gameObject.SetActive(true);
                            if (shipsToHighlight != null && shipsToHighlight.Contains(cc.ref_shipController))
                            {
                                shipListItemController.highlightObject.SetActive(true);
                            }
                        }
                        index++;
                    }
                }
            }

            internal static void CheckForResort()
            {
                if (canvasController == null)
                {
                    Clear();
                    return;
                }
                if (friendlyShip != null && friendlyShip.isDestroyed)
                {
                    friendlyShip = null;
                    rightHandOrdering = null;
                }
                if (enemyShip != null && enemyShip.isDestroyed)
                {
                    enemyShip = null;
                    leftHandOrdering = null;
                }
                UpdateShipLists();
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
                SelectShip(ship, ref enemyShip, ref enemyCandidate, ref lastEnemyShipSelectTime);
            }

            internal static void SelectFriendlyShip(CombatShipController ship)
            {
                SelectShip(ship, ref friendlyShip, ref friendlyCandidate, ref lastFriendlyShipSelectTime);
            }

            internal static void SelectShip(CombatShipController ship, ref CombatShipController selectedShip, ref CombatShipController candidateShip, ref int shipSelectTime)
            {
                int now = Environment.TickCount;

                if (ship == null)
                {
                    Debug.Log("Unselected ship " + P(selectedShip));
                    selectedShip = null;
                    shipSelectTime = 0;
                    candidateShip = null;
                    CheckForResort();
                }
                else
                {
                    if (now - shipSelectTime < 300 && candidateShip == ship)
                    {
                        // unselect by selecting again
                        selectedShip = selectedShip == ship ? null : ship;
                        Debug.Log("Selected ship " + P(selectedShip) + "(" + P(ship) + ")");

                        CheckForResort();
                        shipSelectTime = 0;
                    }
                    else
                    {
                        shipSelectTime = now;
                        candidateShip = ship;
                    }
                }

            }

            private static string P(CombatShipController ship)
            {
                return ship?.ShipState.displayName;
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
                    if (tispaceShipState?.faction != canvasController.activePlayer)
                    {
                        CombatShipController target = (CombatShipController)canvasController.combatMgr.combatantLookup[tispaceShipState];
                        if (TIInputManager.IsControlKeyDown)
                        {
                            SortShipList.SelectEnemyShip(null);
                        }
                        else
                        {
                            SortShipList.SelectEnemyShip(target);
                        }
                    }
                }
            }

            public void OnCombatSecond(CombatSecond e)
            {
                // Debug.Log("CombatSecond " + e);
                // SortShipList.CheckForResort(false); // can't do this, costs way too much
            }

            public void OnGameTimeSpeedChanged(GameTimeSpeedChanged e)
            {
                SortShipList.CheckForResort();
            }
        }


        [HarmonyPatch(typeof(SpaceCombatCanvasController), "DeselectShip")]
        [HarmonyPatch(new Type[] { typeof(CombatShipController) })]
        static class OnDeselectShip
        {
            public static void Postfix(SpaceCombatCanvasController __instance)
            {
                if (SortShipList.friendlyShip != null)
                {
                    Debug.Log("Unselected friendly ship");
                    SortShipList.SelectFriendlyShip(null);
                }
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
                    Debug.Log("Selected friendly ship " + shipController + ", input " + GetKeyDown() ?? "null");
                    SortShipList.SelectFriendlyShip(shipController);
                }
            }

            public static KeyCode? GetKeyDown()
            {
                foreach (var item in Enum.GetValues(typeof(KeyCode)))
                {
                    var key = (KeyCode)item;
                    if (Input.GetKeyDown(key))
                    {
                        return key;
                    }
                }
                return null;
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
