using System;
using System.Collections.Generic;

using UnityEngine;
using PavonisInteractive.TerraInvicta;
using PavonisInteractive.TerraInvicta.SpaceCombat;
using static CombatHacks.ManuverExtensions;

namespace CombatHacks
{

    // A formation lives as long as the leading ship maintains the same head/velocity. As soon as you touch the leading ship, the formup is
    // aborted.
    // Normal usage is to select the group of ships you want to be in the formation, then select the ship that will lead it.
    // the selected ships will then match the velocity to the lead ship, then select the closest free spot in the formation 
    // The formation is normally "tight", it will move the ships to spots as close as they can to the leading ship, without crossing
    // the midplane (this allows you to select a leading ship on the far right and get every other ship to stack up on the left of that
    // ship.
    // formation is always high wall, so five ships stacked high. There is a maximum size of the formation, so about 50 ships or so. 
    // fun stuff 
    public class Formation
    {
        public static Dictionary<TISpaceShipState, Formation> formationMap = new Dictionary<TISpaceShipState, Formation>();

        public static int MAX_FORMATION_SIZE = 205; // should be more than enough ...
        private static readonly int NUM_ROWS = 5; // number of rows in formation
        private static int MAX_RING = MAX_FORMATION_SIZE / NUM_ROWS; // ensure that we can reach all valid position from any position

        public readonly String name;
        public readonly CombatShipController leadingShip;
        private readonly Dictionary<(int, int), FormationNode> slotMap = new Dictionary<(int, int), FormationNode>();
        // note: during the assignment process, slotMap contains nodes that shipMap does note; only after the assignment process will this hold true
        private readonly Dictionary<TISpaceShipState, FormationNode> shipMap = new Dictionary<TISpaceShipState, FormationNode>();
        private readonly Vector3 formationVelocity;
        private readonly Vector3 formationHeading;
        private readonly Quaternion formationRotation;
        private readonly Vector3 horizontalPlane;
        private readonly Vector3 verticalPlane;
        private readonly int[] outsideInRowOrder;
        public float slotSize = 3; // distance between ships ... could actually make this adjustable depending on shipsize and tightness factor

        private readonly List<CombatShipController> queue = new List<CombatShipController>(); // ships waiting to be added to formation
        internal readonly TIDateTime creationTime;
        internal bool sparse;

        private bool destroyed = false;

        public Formation(CombatShipController leadingShip, TIDateTime time)
        {
            this.name = "Force-" + leadingShip.ShipState.displayName;
            this.leadingShip = leadingShip;
            ResetContent();

            creationTime = time;
            sparse = false;
            formationVelocity = leadingShip.velocityAtTime(time.ExportTime());
            formationHeading = leadingShip.headingAtTime(time.ExportTime());
            formationRotation = leadingShip.rotation;
            horizontalPlane = formationRotation * Vector3.right;
            verticalPlane = formationRotation * Vector3.up;

            outsideInRowOrder = new int[NUM_ROWS];
            int order = NUM_ROWS / 2;
            outsideInRowOrder[NUM_ROWS - 1] = 0;
            for (int i = 0; i < NUM_ROWS / 2; i++)
            {
                outsideInRowOrder[i * 2] = -order;
                outsideInRowOrder[i * 2 + 1] = order;
                order--;
            }
            Debug.Log("outsideInOrder: " + P(outsideInRowOrder));

            Debug.Log("Created formation " + name + ", vel " + P(formationVelocity) + ", heading " + P(formationHeading) + ", pos " + P(leadingShip.position));
        }

        // Reset the content of the formation
        private void ResetContent()
        {
            shipMap.Clear();
            slotMap.Clear();
            FormationNode node = new FormationNode((0, 0));
            node.owner = leadingShip;
            shipMap[leadingShip.ShipState] = node;
            slotMap[node.slot] = node;
        }

        public static Formation GetFormation(CombatShipController ship, TIDateTime time)
        {
            if (!formationMap.ContainsKey(ship.ShipState))
            {
                formationMap.Add(ship.ShipState, new Formation(ship, time));
            }
            Debug.Log("GetFormation " + formationMap[ship.ShipState].name);
            return formationMap[ship.ShipState];
        }

        // find the formation that this ship is part of, if any
        internal static Formation FindFormation(TISpaceShipState ship)
        {
            foreach (var f in formationMap.Values)
            {
                if (f.shipMap.ContainsKey(ship))
                {
                    return f;
                }
            }
            return null;
        }

        // formation is valid only as long as the leading ship maintains its original velocity and heading
        public bool IsValid(TIDateTime time)
        {

            return !destroyed && formationVelocity.Equals(leadingShip.velocityAtTime(time.ExportTime())) && formationHeading.Equals(leadingShip.headingAtTime(time.ExportTime()));
        }

        public void AddShip(CombatShipController ship)
        {
            queue.Add(ship);
        }

        // Return true if slot fits into the ring, and if so, which offsets
        private static bool RingOffset(int slot, int ring, out int x, out int y)
        {
            int[] cornerBx = { 1, 1, -1, -1 };
            int[] cornerBy = { -1, 1, 1, -1 };
            int[] cornerDx = { 0, -1, 0, 1 };
            int[] cornerDy = { 1, 0, -1, 0 };

            // split the ring into four walking parts, each part 
            int len = ring * 2;
            int size = (len + 1) * (len + 1);

            // Debug.Log("slot " + slot + ", ring " + ring + ", size " + size);
            if (slot < size)
            {
                if (slot == 0)
                {
                    // avoid divide by zero
                    x = 0;
                    y = 0;
                }
                else
                {
                    int prevSize = ((ring - 1) * 2 + 1) * ((ring - 1) * 2 + 1);
                    int ri = slot - prevSize; // ring index; position inside our ring
                    int corner = ri / len; // which corner
                    int dist = ri % len; // how far from that corner
                    x = cornerBx[corner] * (len - 1) + cornerDx[corner] * dist;
                    y = cornerBy[corner] * (len - 1) + cornerDy[corner] * dist;
                }
                // Debug.Log("-> x " + x + ", y " + y);
                return true;
            }
            else
            {
                x = 0;
                y = 0;
                //Debug.Log("not in ring ");
                return false;
            }
        }


        internal void Destroy()
        {
            Debug.Log("Destroy formation " + name);
            destroyed = true;
            formationMap.Remove(leadingShip.ShipState);
        }
        internal void PackSlots(TIDateTime now)
        {
            int minCol = 0;
            int maxCol = 0;
            foreach (var node in shipMap.Values)
            {
                minCol = Math.Min(node.slot.Item1, minCol);
                maxCol = Math.Max(node.slot.Item1, maxCol);
            }

            // count number of slots in each row
            int[] rowCount = new int[NUM_ROWS];
            foreach (var node in slotMap.Values)
            {
                Debug.Log("slot " + node.slot + " contains " + node.owner.ShipState.displayName);
                rowCount[node.slot.Item2 + 2]++;
            }
            Debug.Log("pack slots, row count " + P(rowCount) + ", minCol " + minCol + ", maxCol " + maxCol);

            // build the push matrix; manual indexing here
            Pusher pusher = new Pusher(rowCount);

            // go through each column from the outside in and do vertical adjustments to get the correct count for in each row

            Debug.Log("AdjustRows to minCol " + minCol);
            for (int col = minCol; col < 0; col++)
            {
                for (int ri = 0; ri < NUM_ROWS; ri++)
                {
                    AdjustRows(pusher, col, ri);
                }
            }
            Debug.Log("AdjustRows to maxCol " + maxCol);
            for (int col = maxCol; col > 0; col--)
            {
                for (int ri = 0; ri < NUM_ROWS; ri++)
                {
                    AdjustRows(pusher, col, ri);
                }
            }
            Debug.Log("PackRows");
            for (int ri = 0; ri < NUM_ROWS; ri++)
            {
                int row = ri - NUM_ROWS / 2;
                PackRow(row, 1, maxCol); // pack positive side towards middle
                PackRow(row, -1, minCol); // pack negative side towards middle
            }
        }

        private void PackRow(int row, int cDir, int limit)
        {
            // pack a single row
            (int, int) packSlot = (0, row);
            for (int col = 0; Math.Abs(col) <= Math.Abs(limit); col += cDir)
            {
                if (packSlot.Item1 == col && slotMap.ContainsKey(packSlot))
                {
                    // skip until we find a packSlot 
                    packSlot = (col + cDir, row);
                    continue;
                }
                // packSlot now valid
                (int, int) slot = (col, row);
                if (slotMap.ContainsKey(slot))
                {
                    MoveNode(slotMap[slot], packSlot);
                    packSlot = (packSlot.Item1 + cDir, row);
                }
            }
        }

        private string P(int[] rowCount)
        {
            string result = "";
            foreach (var r in rowCount)
            {
                result += (result.Length == 0 ? "" + r : ", " + r);
            }
            return "[" + result + "]";
        }

        private void AdjustRows(Pusher pusher, int col, int ri)
        {
            int row = outsideInRowOrder[ri];
            Debug.Log("adjust col " + col + ", r " + ri + "(row " + row + ")");
            var slot = (col, row);
            if (slotMap.ContainsKey(slot))
            {
                var node = slotMap[slot];
                Debug.Log("Try adjust " + (node.owner == null ? null : node.owner.ShipState.displayName) + " @ col " + col + ", row " + row);
                // we may potentially adjust this one 
                int? targetRow = pusher.TryPush(slot);
                if (targetRow != null)
                {
                    // adjust vertically to the targetRow. Find first free column, moving outwards
                    int c = col;
                    var targetSlot = (c, targetRow.Value);
                    while (slotMap.ContainsKey(targetSlot))
                    {
                        c += (c < 0 ? -1 : 1); // move col one step outwards
                        targetSlot = (c, targetRow.Value);
                    }
                    MoveNode(slotMap[slot], targetSlot);
                }
            }
        }

        private void MoveNode(FormationNode node, (int col, int row) slot)
        {
            if (slotMap.ContainsKey(slot))
            {
                Debug.LogError("Slot " + slot + " already taken!");
            }
            else
            {
                Debug.Log("Moving " + node.owner.ShipState.displayName + " from " + node.slot + " to " + slot);
                slotMap.Remove(node.slot);
                slotMap[slot] = node;
                node.slot = slot;
            }
        }

        internal void AssignSlots(TIDateTime now)
        {
            // check if we need to allocate any new members
            if (queue.Count == 0)
            {
                return;
            }
            // we have new members
            // phase 0: 
            // wait until everyone has matched velocity
            // phase 1: 
            // find the plane that we are going to be in, then find the best free slot, add us to that slot
            // phase 2:
            // find the cheapest ship, if its slot is not assigned yet, take it and bump all the other ships
            // in that slot to their next cheapest slot.
            // bumping is going around in a ring, finding the cheapest unassigned. If none found in that ring,
            // try the next ring. going outside the fleet area or crossing side is not allowed.
            //
            foreach (var ship in queue)
            {
                if (!VelocityMatched(ship))
                {
                    Debug.Log("Waiting for " + ship.ShipState.displayName + " to match velocity");
                    return;
                }
            }
            // rely on noone owing the slots
            ResetContent();

            Debug.Log("assign " + queue.Count + " ships at " + now);

            DateTime tNow = now.ExportTime();

            var shipCostMap = new Dictionary<TISpaceShipState, ShipCost>();

            foreach (var ship in queue)
            {
                if (ship == leadingShip)
                {
                    Debug.Log("skipping leading ship");
                    continue; // skip ... or add to zero position?
                }

                // calc shadow of position on the formation plane
                Vector3 offset = leadingShip.position - ship.position;
                float slotX = offset.Dot(horizontalPlane);
                float slotY = offset.Dot(verticalPlane);
                int sx = (int)Math.Round(slotX / slotSize);
                int sy = (int)Math.Round(slotY / slotSize);
                Debug.Log("Assign ship " + ship.ShipState.displayName + " to " + sx + ", " + sy + ", offs " + P(offset) + ", slot " + P(new Vector3(slotX, slotY, 0)));

                // Add the ship as an applicant to the best node
                bool success = AttachShipToClosestFreeSlot(ship, (sx, sy), shipCostMap);
                if (!success)
                {
                    // mark it as failed somehow; very unlikely that it would happen, but ...
                }
            }
            queue.Clear();

            // Phase 2; applicants have applied for their best nodes
            // now we find the ship that has the least cost and actually add it
            while (shipCostMap.Count > 0)
            {
                ShipCost shipCost = null;
                foreach (var sc in shipCostMap.Values)
                {
                    shipCost = (shipCost == null || sc.cost < shipCost.cost) ? sc : shipCost;
                }
                shipCostMap.Remove(shipCost.ship.ShipState);

                (int, int) slot = shipCost.slot;
                if (slotMap.ContainsKey(slot))
                {
                    // must find another slot, this one is taken by someone who pays less
                    // actually, IF we will collide with the ship that already has this slot, we should push that ship one slot away from us ...
                    // but that is only try if we would collide.
                    Debug.Log("bounce " + shipCost.ship.ShipState.displayName + " from " + slot);
                    AttachShipToClosestFreeSlot(shipCost.ship, slot, shipCostMap);
                }
                else
                {
                    // this is the cheapest for us, so take this slot
                    Debug.Log(shipCost.ship.ShipState.displayName + " at cost " + shipCost.cost + " takes " + slot);
                    var node = new FormationNode(slot);
                    slotMap[slot] = node;
                    shipMap[shipCost.ship.ShipState] = node;
                    node.owner = shipCost.ship;
                    shipCostMap.Remove(shipCost.ship.ShipState);
                }
            }

            // Phase 3: packing the nodes... we will pack each left/right half on its own.
            PackSlots(now);
        }

        private bool VelocityMatched(CombatShipController ship)
        {
            ManuverExtensions shipExt = ManuverExtensions.GetManuverExtensions(ship.ShipState);
            IWaypoint wp0 = shipExt.GetWayPoint0();
            float diffMag = (wp0.Velocity - formationVelocity).magnitude;
            Debug.Log("VelMatch for " + ship.ShipState.displayName + ": " + diffMag);
            return diffMag < 0.001;
        }

        private string P(Vector3 offset)
        {
            return ManuverExtensions.P(offset);
        }

        internal Vector3 CalcOffset(TISpaceShipState shipState)
        {
            if (!shipMap.ContainsKey(shipState))
            {
                // should never happen ...
                Debug.Log("No ship " + shipState.displayName + " in formation");
                return Vector3.zero;
            }
            var (x, y) = shipMap[shipState].slot;
            Vector3 rawOffset = new Vector3(x, y, 0); // slot is from leading ship to slot
            Debug.Log("rawOffset " + rawOffset + ", rotated " + (formationRotation * rawOffset));
            return (formationRotation * rawOffset) * slotSize;
        }

        private float CalcCost(ManuverExtensions ext, float x, float y)
        {
            Vector3 offset = new Vector3(x * slotSize, y * slotSize, 0);
            return ext.CalcGoalVector(offset).magnitude;
        }


        private bool AttachShipToClosestFreeSlot(CombatShipController ship, (int, int) slot, Dictionary<TISpaceShipState, ShipCost> shipCostMap)
        {
            int ring = 0;
            float bestCost = 0;
            (int, int)? bestSlot = null;
            ManuverExtensions shipExt = ManuverExtensions.GetManuverExtensions(ship.ShipState);

            for (int i = 0; i < MAX_FORMATION_SIZE; i++)
            {
                int dx, dy;

                if (RingOffset(i, ring, out dx, out dy))
                {
                    int x = slot.Item1 + dx;
                    int y = slot.Item2 + dy;
                    Debug.Log("dx " + dx + ", dy " + dy + ", slot x " + x + ", y " + y + ", bestCost " + bestCost + ", node " + bestSlot);

                    if (IsSlotValid(x, y))
                    {
                        var key = (x, y);
                        Debug.Log("attach dx " + dx + ", dy " + dy + " -> " + key);
                        if (!slotMap.ContainsKey(key))
                        {
                            float cost = CalcCost(shipExt, key.Item1, key.Item2);
                            if (bestSlot == null || cost < bestCost)
                            {
                                bestCost = cost;
                                bestSlot = key;
                                Debug.Log("new best, cost " + bestCost + ", node " + bestSlot);
                            }
                        }
                    }
                }
                else
                {
                    // exited the ring
                    if (bestSlot != null)
                    {
                        Debug.Log("Found node " + bestSlot + " for " + ship.ShipState.displayName + " at cost " + bestCost);
                        shipCostMap[ship.ShipState] = new ShipCost(ship, bestCost, ((int, int))bestSlot);
                        return true;
                    }
                    ring++;
                }
            }
            return false;
        }

        private static bool IsSlotValid(int x, int y)
        {
            return (Math.Abs(x) < MAX_FORMATION_SIZE / 10 && Math.Abs(y) < 3);
        }

        class FormationNode
        {
            // should have a bounding box here ... 
            public (int, int) slot;
            public CombatShipController owner;
            public List<ShipCost> applicants = new List<ShipCost>();

            public FormationNode((int, int) slot)
            {
                this.slot = slot;
            }
        }

        class ShipCost : IComparable<ShipCost>
        {
            public CombatShipController ship;
            public float cost;
            public (int, int) slot;

            public ShipCost(CombatShipController ship, float cost, (int, int) slot)

            {
                this.ship = ship;
                this.cost = cost;
                this.slot = slot;
            }

            public int CompareTo(ShipCost other)
            {
                return other.cost == cost ? 0 : (other.cost < cost ? -1 : 1);
            }
        }

        public class Pusher
        {
            private readonly int[] pushMatrix; // number of pushes from a row to a row, a simulated [fromRow][toRow] matrix, index fromRow * NUM_ROWS + toRow
            private readonly int[] outsideInRowOrder;
            private readonly int[] rowCount;
            private readonly int total;
            private readonly int numRows;

            public Pusher(int[] rowCount)
            {
                this.numRows = rowCount.Length;
                this.rowCount = rowCount;
                this.pushMatrix = new int[numRows * numRows];

                foreach (var c in rowCount)
                {
                    total += c;
                }

                outsideInRowOrder = new int[NUM_ROWS];
                int order = NUM_ROWS / 2;
                outsideInRowOrder[NUM_ROWS - 1] = 0;
                for (int i = 0; i < NUM_ROWS / 2; i++)
                {
                    outsideInRowOrder[i * 2] = -order;
                    outsideInRowOrder[i * 2 + 1] = order;
                    order--;
                }
                CalcPushMatrix();
            }


            private Pusher CalcPushMatrix()
            {
                int min = total / numRows;
                int max = (total + numRows -1) / numRows;
                Debug.Log("CPM min " + min + ", max " + max);
                for (int i = 0; i < numRows; i++)
                {
                    int row = outsideInRowOrder[i];
                    int ri = row + numRows / 2;
                    // push excess to neighbours first
                    int neighbour = 1;
                    while (rowCount[ri] > max)
                    {
                        int ni = ri + neighbour;
                        if (ni >= 0 && ni < numRows)
                        {
                            int maxSubtractable = rowCount[ri] - max;
                            int maxAddable = max - rowCount[ni];
                            Log.Debug("CPM rc[" + ri + "]=" + rowCount[ri] + ", rc["+ ni + "]=" + rowCount[ni] + " maxS " + maxSubtractable + ", maxAdd " + maxAddable);
                            if (maxAddable > 0)
                            {
                                int push = Math.Min(maxAddable, maxSubtractable);
                                int mi = ri * numRows + ni;
                                pushMatrix[mi] = push;
                                rowCount[ri] -= push;
                                rowCount[ni] += push;
                                Log.Debug("push " + push + " from row " + row + "(" + rowCount[ri] + ") to " + (ni - NUM_ROWS / 2)
                                    + "(" + rowCount[ni] + ") (pm[" + mi + "] = " + pushMatrix[mi]);
                            }
                        }
                        if (neighbour >= numRows)
                        {
                            break; // can't push anymore
                        }
                        // 1 -> -1 -> 2 -> -2 -> 3 ...
                        neighbour = neighbour < 0 ? 1 - neighbour : -neighbour;
                    }
                }
                return this;
            }
            public override String ToString()
            {
                String result = "";
                for (int i = 0; i < pushMatrix.Length; i++)
                {
                    if (pushMatrix[i] > 0)
                    {
                        int from = i / numRows;
                        int to = i % numRows;
                        result += (result.Length > 0 ? ", " : "") + from + ":" + pushMatrix[i] + " -> " + to;
                    }
                }
                return result;
            }

            // if slot is pushable, return the row it should be pushed to and remember the push
            internal int? TryPush((int col, int row) slot)
            {
                // look through the pushMatrix row in furthest-first mode to find if we have any pushes left to be made.
                // we do furthest push first to minimize crossing paths ... 
                (int col, int row) = slot;
                int ri = row + NUM_ROWS / 2; // row index
                for (int i = 0; i < NUM_ROWS; i++)
                {
                    int targetRi = FurthestAwayRi(ri, i);
                    int mi = ri * NUM_ROWS + targetRi;
                    Debug.Log("TryPush  col " + col + ", row " + row + ", ri " + ri + ", targetRi " + targetRi + ", mi " + mi + ", matrix " + pushMatrix[mi]);
                    if (pushMatrix[mi] > 0)
                    {
                        // push this one ... not sure if we need to remember the col we pushed from?
                        pushMatrix[mi]--;
                        return targetRi - NUM_ROWS / 2; // return the row
                    }
                }
                return null;
            }

            // find the i'th furhest away row
            private int FurthestAwayRi(int ri, int i)
            {
                int fai = NUM_ROWS - 1;
                int numSkips = i;
                while (true)
                {
                    int resultRi = ri + fai;
                    Debug.Log("FA(" + ri + ", " + i + " = " + resultRi + ", fai " + fai);
                    if (resultRi >= 0 && resultRi < NUM_ROWS)
                    {
                        if (numSkips-- == 0)
                        {
                            return resultRi;
                        }
                    }
                    // 4 -> -4 -> 3 -> -3 ...
                    fai = fai < 0 ? -fai - 1 : -fai;
                    if (fai == 0)
                    {
                        return ri;
                    }
                }
            }
        }

        internal bool IsReady()
        {
            // we are ready when there are no ships waiting to be assigned their formation slot
            // Note that this can go back to beaing unready if ships are added after all ships have been assigned
            // the ready part is really when we are waiting for all ships to match velocity
            return queue.Count == 0;
        }

        internal bool Selectable(CombatShipController selectedShip)
        {
            // a formation is selectable if the selectedShip is either the leadingShip, or has the same heading/velocity as the formation
            if (selectedShip == leadingShip)
            {
                return true;
            }
            var ext = ManuverExtensions.GetManuverExtensions(selectedShip.ShipState);
            if (ext != null)
            {
                IWaypoint wp0 = ext.GetWayPoint0();
                float diffHeading = (wp0.Heading - formationHeading).magnitude;
                float diffVelo = (wp0.Velocity - formationVelocity).magnitude;
                return (diffHeading < 0.001 && diffVelo < 0.001);
            }
            Log.Debug("No MEXT for " + selectedShip.ShipState.displayName);
            return false;

        }

        internal void Exit(CombatShipController ship)
        {
            if (!shipMap.ContainsKey(ship.ShipState))
            {
                // should not happen
                Debug.Log(ship.ShipState.displayName + " is not in formation " + name);
            }
            else
            {
                Debug.Log(ship.ShipState.displayName + " exits formation " + name);
                FormationNode node = shipMap[ship.ShipState];
                shipMap.Remove(ship.ShipState);
                slotMap.Remove(node.slot);
            }
        }
    }
}
