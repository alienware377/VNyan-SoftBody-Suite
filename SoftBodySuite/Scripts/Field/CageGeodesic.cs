using System.Collections.Generic;
using UnityEngine;

namespace SoftBodySuite.Field
{
    // CSR adjacency over the cage, built once per bind. Undirected and deduplicated: each
    // edge appears once in each direction, never twice in the same one.
    public class CageGraph
    {
        public int n;
        public int[] nbrStart;     // CSR offsets, length n+1
        public int[] nbrIdx;       // neighbour node indices
        public float[] nbrLen;     // PER-EDGE REST LENGTH (metres) - not uniform, see below
        public float meanEdge;

        // rest positions, kept so the geodesic can measure how far a contact has MOVED
        // without the caller having to hand them back every frame. Additive to the shared
        // contract; nothing outside this file is required to read it.
        public Vector3[] rest;

        public int Degree(int i) { return nbrStart[i + 1] - nbrStart[i]; }
    }

    // Multi-source geodesic distance over the cage graph: the one spatial falloff every
    // effect shares (ripple rings, jiggle gating, spread).
    //
    // TWO THINGS HERE ARE NOT NEGOTIABLE.
    //
    // 1. PER-EDGE REST LENGTHS. The cage is nominally isotropic at 8 mm, but a remesh that
    //    ran out of its 9 s budget returns a partially-remeshed cage with 3 mm edges next to
    //    40 mm ones in the same graph. Weighting every edge equally turns the "geodesic" into
    //    a HOP COUNT, which is a different field: rings come out elliptical wherever the cage
    //    is anisotropic and the falloff distance means nothing in metres.
    //
    // 2. SEED EVERY NODE ABOVE THE THRESHOLD AT DISTANCE ZERO. A capsule pressed lengthwise
    //    into flesh touches a BAND, not a point. Seeding one centroid produces a circular
    //    ring whose crests are wrong everywhere the contact is not round; seeding the whole
    //    contact set produces the band's own distance field for the same cost (Dijkstra does
    //    not care how many sources it starts with).
    //
    // CACHING IS MANDATORY, NOT AN OPTIMISATION. At ~4500 nodes and ~13500 edges one solve is
    // 0.4-0.9 ms in managed Mono, and the stage wants two fields. Rebuilding both every frame
    // is 1-2 ms of an 8-12 ms budget spent recomputing something that barely changed. So:
    // the contact field rebuilds only when the seeded SET moves materially, and an event field
    // is solved exactly once, when the event is pushed, and memoised until the event dies.
    public class CageGeodesic
    {
        // Unreached nodes get this rather than float.MaxValue. The brief had FieldProxy clamp
        // MaxValue afterwards; writing the finite value here means no consumer can ever
        // multiply MaxValue by anything and get an infinity into the displacement.
        public const float Unreached = 1e6f;

        // The seed cut for the shared CONTACT field. DeformField.seed is |conform * yield| in
        // metres, so 0.1 mm is "this node is genuinely under a collider" rather than "this node
        // caught a rounding crumb of the blur". Every consumer of ContactField has to use the
        // SAME cut or they solve two different fields and the memo thrashes.
        public const float ContactSeedThreshold = 1e-4f;

        const int EventSlots = 8;          // matches DeformField.MaxEvents
        const float SeedCountTol = 0.08f;  // rebuild when the seeded count moves >8%

        CageGraph g;

        float[] dist;                      // contact field (cached)
        bool contactValid;
        int lastSeedCount;
        Vector3 lastSeedCentroid;

        float[][] evDist = new float[EventSlots][];
        int[] evOrigin = new int[EventSlots];
        int evNext;

        // preallocated binary heap with decrease-key
        int[] hNode; float[] hKey; int[] hPos; int hCount;

        // ---------------------------------------------------------------- build
        public static CageGraph BuildGraph(Vector3[] rest, int[] tris)
        {
            if (rest == null || tris == null || rest.Length == 0) return null;
            int n = rest.Length;
            CageGraph g = new CageGraph();
            g.n = n; g.rest = rest;

            // dedupe on a packed (lo,hi) key - the same trick RemeshCage.EdgeTris uses
            HashSet<long> seen = new HashSet<long>();
            int[] deg = new int[n];
            List<long> edges = new List<long>(tris.Length);
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                AddEdge(seen, edges, deg, tris[t], tris[t + 1]);
                AddEdge(seen, edges, deg, tris[t + 1], tris[t + 2]);
                AddEdge(seen, edges, deg, tris[t + 2], tris[t]);
            }

            g.nbrStart = new int[n + 1];
            for (int i = 0; i < n; i++) g.nbrStart[i + 1] = g.nbrStart[i] + deg[i];
            int total = g.nbrStart[n];
            g.nbrIdx = new int[total];
            g.nbrLen = new float[total];
            int[] fill = new int[n];
            float sum = 0f;
            for (int e = 0; e < edges.Count; e++)
            {
                int a = (int)(edges[e] >> 32), b = (int)(edges[e] & 0xFFFFFFFFL);
                float len = (rest[a] - rest[b]).magnitude;
                if (len < 1e-6f) len = 1e-6f;              // a collapsed edge must not be free
                int pa = g.nbrStart[a] + fill[a]++; g.nbrIdx[pa] = b; g.nbrLen[pa] = len;
                int pb = g.nbrStart[b] + fill[b]++; g.nbrIdx[pb] = a; g.nbrLen[pb] = len;
                sum += len;
            }
            g.meanEdge = edges.Count > 0 ? sum / edges.Count : 0.008f;
            return g;
        }

        static void AddEdge(HashSet<long> seen, List<long> edges, int[] deg, int a, int b)
        {
            if (a == b) return;
            int lo = a < b ? a : b, hi = a < b ? b : a;
            long key = ((long)lo << 32) | (uint)hi;
            if (!seen.Add(key)) return;
            edges.Add(key); deg[lo]++; deg[hi]++;
        }

        public void SetGraph(CageGraph graph)
        {
            g = graph;
            Invalidate();
            if (g == null) return;
            if (dist == null || dist.Length != g.n)
            {
                dist = new float[g.n];
                hNode = new int[g.n]; hKey = new float[g.n]; hPos = new int[g.n];
                for (int s = 0; s < EventSlots; s++) evDist[s] = null;
            }
        }

        public CageGraph Graph { get { return g; } }

        public void Invalidate()
        {
            contactValid = false;
            lastSeedCount = -1;
            lastSeedCentroid = new Vector3(1e9f, 1e9f, 1e9f);
            for (int s = 0; s < EventSlots; s++) evOrigin[s] = -1;
            evNext = 0;
        }

        // ---------------------------------------------------------------- contact field
        public bool NeedsRebuild(float[] seed, float seedThreshold)
        {
            if (g == null || seed == null) return false;
            if (!contactValid) return true;
            int cnt; Vector3 c;
            Signature(seed, seedThreshold, out cnt, out c);
            if (cnt == 0) return lastSeedCount != 0;
            int baseCnt = lastSeedCount > 0 ? lastSeedCount : 1;
            int delta = cnt - lastSeedCount; if (delta < 0) delta = -delta;
            if ((float)delta > baseCnt * SeedCountTol) return true;
            return (c - lastSeedCentroid).magnitude > g.meanEdge;
        }

        void Signature(float[] seed, float thr, out int count, out Vector3 centroid)
        {
            count = 0; centroid = Vector3.zero;
            Vector3[] rest = g.rest;
            for (int i = 0; i < g.n; i++)
            {
                if (seed[i] <= thr) continue;
                count++;
                if (rest != null) centroid += rest[i];
            }
            if (count > 0) centroid /= count;
        }

        public float[] SolveMulti(float[] seed, float seedThreshold)
        {
            if (g == null || seed == null) return null;
            if (!NeedsRebuild(seed, seedThreshold)) return dist;

            int cnt; Vector3 c;
            Signature(seed, seedThreshold, out cnt, out c);
            lastSeedCount = cnt; lastSeedCentroid = c; contactValid = true;

            HeapClear();
            for (int i = 0; i < g.n; i++)
            {
                if (seed[i] > seedThreshold) { dist[i] = 0f; HeapPush(i, 0f); }
                else dist[i] = Unreached;
            }
            Run(dist);
            return dist;
        }

        public float[] ContactField { get { return dist; } }

        // Whether the LAST SolveMulti had anything to seed from. `dist` is allocated by
        // SetGraph and is therefore non-null and all-ZERO before the first solve, which reads
        // as "every node is exactly at the contact" — the difference between "no contact" and
        // "contact everywhere" is not visible in the array itself, so it is published here.
        public bool ContactSeeded { get { return contactValid && lastSeedCount > 0; } }

        // ---------------------------------------------------------------- event fields
        // Solved once per event origin and memoised. A caller that pushes the same origin
        // twice (a second impact on the same spot) gets the cached field back for free.
        public float[] SolveSingle(int originNode)
        {
            if (g == null || originNode < 0 || originNode >= g.n) return null;
            for (int s = 0; s < EventSlots; s++)
                if (evOrigin[s] == originNode && evDist[s] != null) return evDist[s];

            int slot = evNext;
            evNext = (evNext + 1) % EventSlots;
            if (evDist[slot] == null || evDist[slot].Length != g.n) evDist[slot] = new float[g.n];
            float[] d = evDist[slot];
            evOrigin[slot] = originNode;

            HeapClear();
            for (int i = 0; i < g.n; i++) d[i] = Unreached;
            d[originNode] = 0f; HeapPush(originNode, 0f);
            Run(d);
            return d;
        }

        public float[] EventField(int eventSlot)
        {
            if (eventSlot < 0 || eventSlot >= EventSlots) return null;
            return evDist[eventSlot];
        }

        // which memo slot holds this origin, or -1 - lets an event holder keep a stable handle
        public int EventSlotFor(int originNode)
        {
            for (int s = 0; s < EventSlots; s++) if (evOrigin[s] == originNode) return s;
            return -1;
        }

        public void ReleaseEvent(int originNode)
        {
            for (int s = 0; s < EventSlots; s++) if (evOrigin[s] == originNode) evOrigin[s] = -1;
        }

        public int NearestNode(Vector3 p, Vector3[] pos)
        {
            if (g == null) return 0;
            Vector3[] src = pos != null ? pos : g.rest;
            if (src == null) return 0;
            int best = 0; float bd = float.MaxValue;
            int n = Mathf.Min(g.n, src.Length);
            for (int i = 0; i < n; i++)
            {
                float d2 = (src[i] - p).sqrMagnitude;
                if (d2 >= bd) continue;
                bd = d2; best = i;
            }
            return best;
        }

        // ---------------------------------------------------------------- solver
        void Run(float[] d)
        {
            while (hCount > 0)
            {
                int u = HeapPop();
                float du = d[u];
                int s = g.nbrStart[u], e = g.nbrStart[u + 1];
                for (int j = s; j < e; j++)
                {
                    int v = g.nbrIdx[j];
                    float nd = du + g.nbrLen[j];
                    if (nd >= d[v]) continue;
                    d[v] = nd;
                    HeapPush(v, nd);           // push doubles as decrease-key
                }
            }
        }

        // ---------------------------------------------------------------- heap
        void HeapClear()
        {
            hCount = 0;
            for (int i = 0; i < g.n; i++) hPos[i] = -1;
        }

        void HeapPush(int node, float key)
        {
            int p = hPos[node];
            if (p >= 0)
            {
                if (key >= hKey[p]) return;
                hKey[p] = key; SiftUp(p);
                return;
            }
            p = hCount++;
            hNode[p] = node; hKey[p] = key; hPos[node] = p;
            SiftUp(p);
        }

        int HeapPop()
        {
            int top = hNode[0];
            hPos[top] = -1;
            hCount--;
            if (hCount > 0)
            {
                hNode[0] = hNode[hCount]; hKey[0] = hKey[hCount];
                hPos[hNode[0]] = 0;
                SiftDown(0);
            }
            return top;
        }

        void SiftUp(int i)
        {
            int node = hNode[i]; float key = hKey[i];
            while (i > 0)
            {
                int par = (i - 1) >> 1;
                if (hKey[par] <= key) break;
                hNode[i] = hNode[par]; hKey[i] = hKey[par]; hPos[hNode[i]] = i;
                i = par;
            }
            hNode[i] = node; hKey[i] = key; hPos[node] = i;
        }

        void SiftDown(int i)
        {
            int node = hNode[i]; float key = hKey[i];
            for (; ; )
            {
                int l = 2 * i + 1;
                if (l >= hCount) break;
                int r = l + 1;
                int m = (r < hCount && hKey[r] < hKey[l]) ? r : l;
                if (hKey[m] >= key) break;
                hNode[i] = hNode[m]; hKey[i] = hKey[m]; hPos[hNode[i]] = i;
                i = m;
            }
            hNode[i] = node; hKey[i] = key; hPos[node] = i;
        }
    }
}
