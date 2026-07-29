using System.Collections.Generic;
using UnityEngine;

namespace WobbleStudio
{
    // A whole skinned mesh used as a collider: baked vertices -> spatial-hashed point cloud.
    public class MeshColliderCloud
    {
        public List<Vector3> pts = new List<Vector3>();
        public float radius = 0.015f;
        Dictionary<long, List<int>> hash = new Dictionary<long, List<int>>();
        float cell;

        public void Build(float rad)
        {
            radius = Mathf.Max(0.004f, rad);
            cell = radius * 2f;
            hash.Clear();
            for (int i = 0; i < pts.Count; i++)
            {
                long k = Key(Mathf.FloorToInt(pts[i].x / cell), Mathf.FloorToInt(pts[i].y / cell), Mathf.FloorToInt(pts[i].z / cell));
                List<int> lst; if (!hash.TryGetValue(k, out lst)) { lst = new List<int>(); hash[k] = lst; }
                lst.Add(i);
            }
        }

        // EXACT deepest push-out in metres (projects p onto the cloud surface)
        public bool TryPush(Vector3 p, out Vector3 push)
        {
            Vector3 pn;
            if (!TryPushNorm(p, out pn)) { push = Vector3.zero; return false; }
            push = pn * radius;
            return true;
        }

        // deepest push-out for p, NORMALISED 0..1 by radius (dir * pen/radius)
        public bool TryPushNorm(Vector3 p, out Vector3 pushNorm)
        {
            pushNorm = Vector3.zero;
            if (pts.Count == 0) return false;
            int cx = Mathf.FloorToInt(p.x / cell), cy = Mathf.FloorToInt(p.y / cell), cz = Mathf.FloorToInt(p.z / cell);
            float best = 0f; Vector3 bestDir = Vector3.zero;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                List<int> lst; if (!hash.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out lst)) continue;
                for (int j = 0; j < lst.Count; j++)
                {
                    Vector3 d = p - pts[lst[j]];
                    float dist = d.magnitude;
                    if (dist < radius && dist > 1e-6f)
                    {
                        float f = 1f - dist / radius;
                        if (f > best) { best = f; bestDir = d / dist; }
                    }
                }
            }
            if (best <= 0f) return false;
            pushNorm = bestDir * best;
            return true;
        }

        static long Key(int x, int y, int z)
        {
            return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
        }
    }

    // Per-region soft-body solver on WELDED position nodes (duplicate vertices along UV
    // seams move as one — simulating them independently tears the surface).
    //
    // Two-part update each frame:
    //   StepDynamics (substepped): springs, inertia, gravity, waves, oscillators.
    //   FieldAndWrite (once):      smooth collision field + cohesion + final output.
    public class SquishSim
    {
        public SquishRegion cfg;

        public int[] idx;            // representative mesh vertex per node
        public float[] w;            // node paint weight
        int[][] members;             // all duplicates per node
        int n;

        Vector3[] offset, vel, prevTarget;
        bool primed;
        int[][] nbr;
        float simT;

        // collision: separate smooth non-accumulating layer (UNITLESS field 0..~1,
        // scaled to metres only at output — so dent depth is independent of collider
        // radius AND of the jiggle maxOffset clamp)
        Vector3[] colCur, colTarget;
        Vector3[] colBase;           // per-node adaptive baseline (pose/breathing absorbed)
        float prevPushMag;

        float[] clothH, clothV;
        Vector3 jelloPos, jelloVel;
        float[] jelloF;
        Vector3 jelloC0; float jelloRMax; float jelloFT;   // jell-o randomizer (wandering centre)
        Vector3 swayPos, swayVel;
        float twist, twistVel;
        struct Wave { public Vector3 pos; public float t; public float amp; }
        readonly List<Wave> waves = new List<Wave>();
        float waveCooldown;
        float[] cellNoise;
        float cellNoiseSize = -1f;

        // carried from dynamics to output
        Vector3 lastLocalDown, lastCentroid, lastCentroidVel;

        // coarse RBF clusters for collision smoothing: the contact field is projected onto
        // ~dozens of control points spread over the region and interpolated back — giving
        // big smooth blobby dents REGARDLESS of mesh density (graph diffusion smoothed only
        // a few millimetres on dense meshes, which read as facet-level "jello destruction").
        int K;
        Vector3[] clusterC;
        int[][] ncIdx; float[][] ncW;         // per-node: up to 4 clusters + weights
        int[][] cNbr; float[][] cNbrW;        // cluster-to-cluster smoothing weights
        Vector3[] cAcc, cVec; float[] cWsum;

        public Transform refBone;
        public Quaternion refRest;
        public bool refCaptured;

        public Vector3 boundCenter;
        public float boundRadius;

        public Transform[] colTr;
        public SquishCollider[] colCfg;
        public MeshColliderCloud[] clouds;

        public void Build(SquishRegion region, Mesh restMesh, int[] weldOf, List<int>[] weldMembers)
        {
            cfg = region;
            simT = 0f;

            Dictionary<int, int> nodeOfWeld = new Dictionary<int, int>();
            List<int> repList = new List<int>();
            List<float> wList = new List<float>();
            for (int i = 0; i < region.vertIndex.Count; i++)
            {
                int vi = region.vertIndex[i];
                if (vi >= weldOf.Length) continue;
                int wg = weldOf[vi];
                int node;
                if (!nodeOfWeld.TryGetValue(wg, out node))
                {
                    node = repList.Count;
                    nodeOfWeld[wg] = node;
                    repList.Add(weldMembers[wg][0]);
                    wList.Add(region.weight[i]);
                }
                else if (region.weight[i] > wList[node]) wList[node] = region.weight[i];
            }
            n = repList.Count;
            idx = repList.ToArray(); w = wList.ToArray();
            members = new int[n][];
            foreach (KeyValuePair<int, int> kv in nodeOfWeld)
                members[kv.Value] = weldMembers[kv.Key].ToArray();

            offset = new Vector3[n]; vel = new Vector3[n]; prevTarget = new Vector3[n];
            colCur = new Vector3[n]; colTarget = new Vector3[n]; colBase = new Vector3[n];
            prevPushMag = 0f;
            clothH = new float[n]; clothV = new float[n]; jelloF = new float[n];
            jelloPos = Vector3.zero; jelloVel = Vector3.zero;
            swayPos = Vector3.zero; swayVel = Vector3.zero; twist = 0f; twistVel = 0f;
            primed = false; waves.Clear(); cellNoiseSize = -1f;

            List<int>[] adj = new List<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<int>(6);
            int[] tris = restMesh.triangles;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int na, nb, nc;
                bool ha = nodeOfWeld.TryGetValue(weldOf[tris[t]], out na);
                bool hb = nodeOfWeld.TryGetValue(weldOf[tris[t + 1]], out nb);
                bool hc = nodeOfWeld.TryGetValue(weldOf[tris[t + 2]], out nc);
                if (ha && hb && na != nb) AddEdge(adj, na, nb);
                if (hb && hc && nb != nc) AddEdge(adj, nb, nc);
                if (ha && hc && na != nc) AddEdge(adj, na, nc);
            }
            nbr = new int[n][];
            for (int i = 0; i < n; i++) nbr[i] = adj[i].ToArray();
        }

        static void AddEdge(List<int>[] adj, int a, int b)
        {
            if (!adj[a].Contains(b)) adj[a].Add(b);
            if (!adj[b].Contains(a)) adj[b].Add(a);
        }

        float diagTimer;

        void BuildClusters(Vector3[] baked, float rMax)
        {
            float cell = Mathf.Max(0.01f, rMax / 3.2f);
            Dictionary<long, int> grid = new Dictionary<long, int>();
            List<Vector3> sums = new List<Vector3>();
            List<int> counts = new List<int>();
            for (int i = 0; i < n; i++)
            {
                Vector3 p = baked[idx[i]];
                long k = ((long)(Mathf.FloorToInt(p.x / cell) & 0xFFFFF) << 40)
                       | ((long)(Mathf.FloorToInt(p.y / cell) & 0xFFFFF) << 20)
                       | (long)(Mathf.FloorToInt(p.z / cell) & 0xFFFFF);
                int c;
                if (!grid.TryGetValue(k, out c)) { c = sums.Count; grid[k] = c; sums.Add(Vector3.zero); counts.Add(0); }
                sums[c] += p; counts[c]++;
            }
            K = sums.Count;
            clusterC = new Vector3[K];
            for (int c = 0; c < K; c++) clusterC[c] = sums[c] / Mathf.Max(1, counts[c]);
            cAcc = new Vector3[K]; cVec = new Vector3[K]; cWsum = new float[K];

            float sig = cell; float inv2s2 = 1f / (2f * sig * sig);

            // cluster-to-cluster smoothing weights (gaussian by centre distance, incl. self)
            cNbr = new int[K][]; cNbrW = new float[K][];
            for (int a = 0; a < K; a++)
            {
                List<int> ids = new List<int>(); List<float> ws = new List<float>();
                for (int b = 0; b < K; b++)
                {
                    float d2 = (clusterC[a] - clusterC[b]).sqrMagnitude;
                    if (d2 > (2.2f * cell) * (2.2f * cell)) continue;
                    ids.Add(b); ws.Add(Mathf.Exp(-d2 * inv2s2));
                }
                float tot = 0f; for (int j = 0; j < ws.Count; j++) tot += ws[j];
                for (int j = 0; j < ws.Count; j++) ws[j] /= Mathf.Max(1e-6f, tot);
                cNbr[a] = ids.ToArray(); cNbrW[a] = ws.ToArray();
            }

            // per-node: top-4 nearest clusters with gaussian weights (normalised)
            ncIdx = new int[n][]; ncW = new float[n][];
            for (int i = 0; i < n; i++)
            {
                Vector3 p = baked[idx[i]];
                int[] bi = { -1, -1, -1, -1 }; float[] bw = { 0f, 0f, 0f, 0f };
                for (int c = 0; c < K; c++)
                {
                    float g = Mathf.Exp(-(p - clusterC[c]).sqrMagnitude * inv2s2);
                    for (int s = 0; s < 4; s++)
                        if (g > bw[s])
                        {
                            for (int t = 3; t > s; t--) { bw[t] = bw[t - 1]; bi[t] = bi[t - 1]; }
                            bw[s] = g; bi[s] = c; break;
                        }
                }
                float sum = bw[0] + bw[1] + bw[2] + bw[3];
                int cnt = 0; for (int s = 0; s < 4; s++) if (bi[s] >= 0 && bw[s] > 1e-5f) cnt++;
                ncIdx[i] = new int[cnt]; ncW[i] = new float[cnt];
                int at = 0;
                for (int s = 0; s < 4; s++)
                    if (bi[s] >= 0 && bw[s] > 1e-5f) { ncIdx[i][at] = bi[s]; ncW[i][at] = bw[s] / Mathf.Max(1e-6f, sum); at++; }
            }
        }

        // ==================== dynamics (substepped) ====================
        public void StepDynamics(Vector3[] baked, Vector3[] normals, float dt, Vector3 localDown, Transform proxy)
        {
            if (n == 0) return;
            simT += dt;
            lastLocalDown = localDown;

            if (!primed)
            {
                for (int i = 0; i < n; i++) prevTarget[i] = baked[idx[i]];
                Vector3 c0 = Vector3.zero;
                for (int i = 0; i < n; i++) c0 += baked[idx[i]];
                c0 /= n;
                float rMax = 0.0001f;
                for (int i = 0; i < n; i++)
                { float r = (baked[idx[i]] - c0).magnitude; if (r > rMax) rMax = r; }
                for (int i = 0; i < n; i++)
                    jelloF[i] = Mathf.Cos(Mathf.PI * (baked[idx[i]] - c0).magnitude / rMax);
                jelloC0 = c0; jelloRMax = rMax;
                BuildClusters(baked, rMax);
                primed = true;
            }

            float poseFactor = 1f;
            if (cfg.gravityPoseOnly)
            {
                poseFactor = 0f;
                if (refBone != null && refCaptured)
                    poseFactor = Mathf.Clamp01(Quaternion.Angle(refRest, refBone.localRotation) / 45f);
            }
            Vector3 grav = localDown * (cfg.gravity * 0.6f * poseFactor);

            float stiff = Mathf.Max(0.5f, cfg.stiffness);
            float damp = Mathf.Clamp01(cfg.damping);
            float clothC2 = Mathf.Lerp(40f, 400f, Mathf.Clamp01(cfg.clothSize)) * Mathf.Max(0.05f, cfg.waveSpeed);
            float clothDampMul = Mathf.Exp(-2.5f * dt);
            float jelloOmega = 2f * Mathf.PI * Mathf.Lerp(6f, 1.5f, Mathf.Clamp01(cfg.jelloSize)) * Mathf.Max(0.05f, cfg.waveSpeed);

            Vector3 centroid = Vector3.zero, prevCentroid = Vector3.zero;
            for (int i = 0; i < n; i++) { centroid += baked[idx[i]]; prevCentroid += prevTarget[i]; }
            centroid /= n; prevCentroid /= n;
            Vector3 dc = centroid - prevCentroid;
            bool teleport = dc.sqrMagnitude > 0.04f;
            Vector3 centroidVel = teleport ? Vector3.zero : dc / Mathf.Max(dt, 1e-5f);
            lastCentroid = centroid; lastCentroidVel = centroidVel;

            waveCooldown -= dt;
            if (cfg.liquid > 0.001f && waveCooldown <= 0f && centroidVel.magnitude > 0.25f)
            {
                Wave wv; wv.pos = centroid; wv.t = 0f;
                wv.amp = Mathf.Min(0.03f, centroidVel.magnitude * 0.01f) * cfg.liquid;
                waves.Add(wv); if (waves.Count > 6) waves.RemoveAt(0);
                waveCooldown = 0.15f;
            }
            for (int k = waves.Count - 1; k >= 0; k--)
            {
                Wave wv = waves[k]; wv.t += dt * Mathf.Max(0.05f, cfg.waveSpeed); waves[k] = wv;
                if (wv.t > 2.5f) waves.RemoveAt(k);
            }

            if (cfg.jello > 0.001f)
            {
                // RANDOMIZER: slowly wander the wobble centre horizontally (smooth Perlin
                // drift) so the jell-o mode stops looking like a fixed standing wave.
                // Refreshing the falloff field once per frame (simT throttle: StepDynamics
                // is substepped) — n cosines, cheap.
                if (cfg.jelloRandom > 0.001f && jelloRMax > 1e-4f && simT - jelloFT > 0.014f)
                {
                    jelloFT = simT;
                    float jr = Mathf.Clamp01(cfg.jelloRandom);
                    float tt = simT * (0.2f + 0.6f * jr);
                    Vector3 jdrift = new Vector3(Mathf.PerlinNoise(tt, 13.7f) - 0.5f, 0f,
                                                 Mathf.PerlinNoise(41.3f, tt) - 0.5f) * (1.8f * jr * jelloRMax);
                    Vector3 jcc = jelloC0 + jdrift;
                    for (int i = 0; i < n; i++)
                        jelloF[i] = Mathf.Cos(Mathf.PI * (baked[idx[i]] - jcc).magnitude / jelloRMax);
                }
                if (!teleport) jelloVel -= dc * 14f;
                jelloVel += -jelloOmega * jelloOmega * jelloPos * dt;
                jelloVel *= Mathf.Exp(-2.0f * dt);
                jelloPos += jelloVel * dt;
                float jm = jelloPos.magnitude; if (jm > 0.1f) jelloPos *= 0.1f / jm;
            }
            else { jelloPos = Vector3.zero; jelloVel = Vector3.zero; }

            if (cfg.sway > 0.001f)
            {
                Vector3 lat = dc - localDown * Vector3.Dot(dc, localDown);
                if (!teleport) swayVel -= lat * 10f;
                float swOmega = 2f * Mathf.PI * 1.3f * Mathf.Max(0.05f, cfg.waveSpeed);
                swayVel += -swOmega * swOmega * swayPos * dt;
                swayVel *= Mathf.Exp(-1.6f * dt);
                swayPos += swayVel * dt;
                float sm = swayPos.magnitude; if (sm > 0.08f) swayPos *= 0.08f / sm;
            }
            else { swayPos = Vector3.zero; swayVel = Vector3.zero; }

            if (cfg.twistJiggle > 0.001f)
            {
                Vector3 lat = dc - localDown * Vector3.Dot(dc, localDown);
                Vector3 fwd = Vector3.Cross(localDown, Vector3.right);
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.Cross(localDown, Vector3.forward);
                if (!teleport) twistVel -= Vector3.Dot(Vector3.Cross(localDown, lat), fwd.normalized) * 40f;
                float twOmega = 2f * Mathf.PI * 2.0f * Mathf.Max(0.05f, cfg.waveSpeed);
                twistVel += -twOmega * twOmega * twist * dt;
                twistVel *= Mathf.Exp(-1.8f * dt);
                twist += twistVel * dt;
                twist = Mathf.Clamp(twist, -0.6f, 0.6f);
            }
            else { twist = 0f; twistVel = 0f; }

            if (cfg.cellulite > 0.0001f && cellNoiseSize != cfg.celluliteSize)
            {
                cellNoise = new float[n];
                float freq = Mathf.Lerp(220f, 25f, Mathf.Clamp01(cfg.celluliteSize));
                for (int i = 0; i < n; i++)
                {
                    Vector3 p0 = baked[idx[i]];
                    float v1 = Mathf.PerlinNoise(p0.x * freq, p0.y * freq * 0.9f + p0.z * freq * 0.7f);
                    float v2 = Mathf.PerlinNoise(p0.z * freq * 1.3f + 11.7f, p0.x * freq * 1.1f + 5.3f);
                    cellNoise[i] = (v1 + v2) * 0.5f - 0.5f;
                }
                cellNoiseSize = cfg.celluliteSize;
            }

            float maxOff = Mathf.Max(0.001f, cfg.maxOffset);
            float dampMul = Mathf.Pow(1f - damp, dt * 30f);
            float r2max = 0f;

            for (int i = 0; i < n; i++)
            {
                int vi = idx[i];
                if (vi >= baked.Length) continue;
                Vector3 target = baked[vi];
                Vector3 db = target - prevTarget[i];
                prevTarget[i] = target;
                if (db.sqrMagnitude > 0.04f) db = Vector3.zero;

                vel[i] -= db * (cfg.bounce * 18f * w[i]);

                Vector3 F = offset[i] * (-stiff) + grav * w[i];
                vel[i] += F * dt;
                vel[i] *= dampMul;
                offset[i] += vel[i] * dt;

                float m = offset[i].magnitude;
                if (m > maxOff) { offset[i] *= maxOff / m; vel[i] *= 0.5f; }

                if (cfg.clothRipple > 0.001f && nbr[i].Length > 0)
                {
                    clothV[i] -= Vector3.Dot(db, normals[vi]) * 6f * w[i];
                    float lapH = 0f;
                    int[] nb = nbr[i];
                    for (int j = 0; j < nb.Length; j++) lapH += clothH[nb[j]];
                    lapH = lapH / nb.Length - clothH[i];
                    clothV[i] += (clothC2 * lapH - 30f * clothH[i]) * dt;
                    clothV[i] *= clothDampMul;
                    clothH[i] += clothV[i] * dt;
                    clothH[i] = Mathf.Clamp(clothH[i], -maxOff, maxOff);
                }
                else clothH[i] = 0f;

                float rr = (target + offset[i] - lastCentroid).sqrMagnitude;
                if (rr > r2max) r2max = rr;
            }
            boundCenter = lastCentroid;
            boundRadius = Mathf.Sqrt(r2max);
        }

        // ==================== collision field + output (once per frame) ====================
        public void FieldAndWrite(Vector3[] baked, Vector3[] normals, Vector3[] disp,
                                  float dt, List<SquishSim> others, Transform proxy)
        {
            if (n == 0 || !primed) return;
            Vector3 localDown = lastLocalDown;
            float maxOff = Mathf.Max(0.001f, cfg.maxOffset);

            // cohesion on the jiggle offsets (keeps the blob connected)
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < n; i++)
                {
                    int[] nb = nbr[i];
                    if (nb.Length == 0) continue;
                    Vector3 avg = Vector3.zero;
                    for (int j = 0; j < nb.Length; j++) avg += offset[nb[j]];
                    avg /= nb.Length;
                    offset[i] = Vector3.Lerp(offset[i], avg, 0.22f);
                }

            // ---- raw UNITLESS penetration field (0..1 per collider, dir * pen/radius) ----
            System.Array.Clear(colTarget, 0, colTarget.Length);
            int ncol = 0; // collision lives in Squish Studio — this plugin is jiggle-only
            for (int c = 0; c < ncol; c++)
            {
                if (!colCfg[c].enabled) continue;
                bool isMesh = !string.IsNullOrEmpty(colCfg[c].mesh);
                MeshColliderCloud cl = (isMesh && clouds != null && c < clouds.Length) ? clouds[c] : null;
                Transform ct = colTr != null ? colTr[c] : null;
                if (isMesh && cl == null) continue;
                if (!isMesh && ct == null) continue;

                Vector3 cp0 = Vector3.zero, cp1 = Vector3.zero; float rad = 0f;
                if (!isMesh)
                {
                    cp0 = proxy.InverseTransformPoint(ct.position);
                    rad = colCfg[c].radius / Mathf.Max(0.0001f, proxy.lossyScale.x);
                    cp1 = colCfg[c].length > 0f
                        ? proxy.InverseTransformPoint(ct.position + ct.forward * colCfg[c].length) : cp0;
                }

                for (int i = 0; i < n; i++)
                {
                    Vector3 p = baked[idx[i]];
                    if (isMesh)
                    {
                        Vector3 pn;
                        if (cl.TryPushNorm(p, out pn)) colTarget[i] += pn;
                    }
                    else
                    {
                        Vector3 cp = colCfg[c].length > 0f ? ClosestOnSegment(cp0, cp1, p) : cp0;
                        Vector3 d = p - cp; float dist = d.magnitude;
                        if (dist < rad && dist > 1e-6f) colTarget[i] += d / dist * (1f - dist / rad);
                    }
                }
            }

            // region-vs-region (breasts pressing together), unitless
            if (false && cfg.selfSquish > 0.001f && others != null)
            {
                float lvl = Mathf.Clamp(cfg.selfSquish, 0f, 2f) * 0.5f;
                for (int o = 0; o < others.Count; o++)
                {
                    SquishSim os = others[o];
                    if (os == this || os.boundRadius <= 0f || boundRadius <= 0f) continue;
                    Vector3 dC = boundCenter - os.boundCenter;
                    float dist = dC.magnitude;
                    float span = (boundRadius + os.boundRadius) * 0.8f;
                    float overlap = span - dist;
                    if (overlap <= 0f || dist < 1e-5f) continue;
                    Vector3 dir = dC / dist;
                    float inv2s2 = 1f / (2f * os.boundRadius * os.boundRadius + 1e-6f);
                    float ovNorm = Mathf.Clamp01(overlap / Mathf.Max(0.02f, span * 0.5f));
                    for (int i = 0; i < n; i++)
                    {
                        float d2 = (baked[idx[i]] - os.boundCenter).sqrMagnitude;
                        colTarget[i] += dir * (ovNorm * lvl * Mathf.Exp(-d2 * inv2s2));
                    }
                }
            }

            float dbgRaw = 0f;
            for (int i = 0; i < n; i++) { float m2 = colTarget[i].sqrMagnitude; if (m2 > dbgRaw) dbgRaw = m2; }

            // per-node cap + ADAPTIVE baseline (sustained pose/breathing contact absorbed
            // slowly; new pokes act instantly; released contact recovers fast)
            float upA = 1f - Mathf.Exp(-dt / 6f);
            float dnA = 1f - Mathf.Exp(-dt / 0.5f);
            for (int i = 0; i < n; i++)
            {
                Vector3 raw = colTarget[i];
                float rm = raw.magnitude;
                if (rm > 1.5f) { raw *= 1.5f / rm; rm = 1.5f; }
                bool rising = rm * rm > colBase[i].sqrMagnitude;
                colBase[i] = Vector3.Lerp(colBase[i], raw, rising ? upA : dnA);
                float effM = Mathf.Max(0f, rm - colBase[i].magnitude);
                colTarget[i] = rm > 1e-6f ? raw * (effM / rm) : Vector3.zero;
            }

            float dbgEff = 0f;
            for (int i = 0; i < n; i++) { float m2 = colTarget[i].sqrMagnitude; if (m2 > dbgEff) dbgEff = m2; }

            // ---- cluster RBF smoothing: project the contact field onto coarse control
            // points and interpolate back — a big soft blobby dent at ANY mesh density,
            // peak-preserved so smoothing widens the dent without flattening it.
            if (K > 0 && ncIdx != null)
            {
                float peakBefore = 0f;
                for (int i = 0; i < n; i++) { float m2 = colTarget[i].sqrMagnitude; if (m2 > peakBefore) peakBefore = m2; }

                // MAX-projection: each cluster takes its strongest contact (an averaging
                // projection diluted sparse contact ~15x — measured raw 0.96 -> rbf 0.05)
                System.Array.Clear(cVec, 0, K);
                for (int i = 0; i < n; i++)
                {
                    int[] ci = ncIdx[i]; float[] cw = ncW[i];
                    for (int j = 0; j < ci.Length; j++)
                    {
                        Vector3 cand = colTarget[i] * cw[j];
                        if (cand.sqrMagnitude > cVec[ci[j]].sqrMagnitude) cVec[ci[j]] = cand;
                    }
                }
                // one smoothing pass across clusters
                for (int c = 0; c < K; c++)
                {
                    Vector3 s = Vector3.zero;
                    int[] ids = cNbr[c]; float[] ws = cNbrW[c];
                    for (int j = 0; j < ids.Length; j++) s += cVec[ids[j]] * ws[j];
                    cAcc[c] = s;
                }
                for (int i = 0; i < n; i++)
                {
                    int[] ci = ncIdx[i]; float[] cw = ncW[i];
                    Vector3 v = Vector3.zero;
                    for (int j = 0; j < ci.Length; j++) v += cAcc[ci[j]] * cw[j];
                    colTarget[i] = v;
                }

                if (peakBefore > 1e-10f)
                {
                    float peakAfter = 0f;
                    for (int i = 0; i < n; i++) { float m2 = colTarget[i].sqrMagnitude; if (m2 > peakAfter) peakAfter = m2; }
                    float boost = Mathf.Min(6f, Mathf.Sqrt(peakBefore) / Mathf.Max(1e-5f, Mathf.Sqrt(peakAfter)));
                    for (int i = 0; i < n; i++) colTarget[i] *= boost;
                }
            }

            float dbgRbf = 0f;
            for (int i = 0; i < n; i++) { float m2 = colTarget[i].sqrMagnitude; if (m2 > dbgRbf) dbgRbf = m2; }

            // depth scale: user override, else proportional to the REGION SIZE
            float dentMax = cfg.squishDepth > 0.001f
                ? cfg.squishDepth
                : Mathf.Clamp(boundRadius * 0.4f, 0.02f, 0.08f);
            float squishS = Mathf.Clamp(cfg.squish, 0f, 2f);
            float rigidS = Mathf.Clamp(cfg.bulge, 0f, 2f);

            Vector3 meanPush = Vector3.zero;
            for (int i = 0; i < n; i++) meanPush += colTarget[i];
            meanPush /= n;
            float pushMag = meanPush.magnitude;
            if (pushMag - prevPushMag > 0.005f && pushMag > 1e-5f)
            {
                Vector3 kd = meanPush / pushMag;
                for (int i = 0; i < n; i++) vel[i] += kd * ((pushMag - prevPushMag) * dentMax * 25f * w[i]);
            }
            prevPushMag = pushMag;

            // sideways volume bulge: flesh displaced by the contact flows outward around it
            Vector3 contactC = Vector3.zero; float contactW = 0f;
            for (int i = 0; i < n; i++)
            {
                float m = colTarget[i].magnitude;
                contactC += baked[idx[i]] * m; contactW += m;
            }
            if (contactW > 1e-5f) contactC /= contactW;
            Vector3 meanDir = pushMag > 1e-6f ? meanPush / pushMag : Vector3.zero;
            if (contactW > 1e-5f && pushMag > 0.01f && cfg.bulge > 0.001f)
            {
                float sigma = Mathf.Max(0.03f, boundRadius * 0.8f);
                float inv2s2 = 1f / (2f * sigma * sigma);
                for (int i = 0; i < n; i++)
                {
                    Vector3 radial = baked[idx[i]] - contactC;
                    radial -= meanDir * Vector3.Dot(radial, meanDir);   // sideways only
                    float rl = radial.magnitude;
                    if (rl < 1e-5f) continue;
                    float g = Mathf.Exp(-rl * rl * inv2s2);
                    colTarget[i] += radial / rl * (pushMag * rigidS * 0.9f * g);
                }
            }

            float ease = 1f - Mathf.Exp(-14f * dt);
            float colCap = dentMax * 1.3f;
            for (int i = 0; i < n; i++)
            {
                // soft layer: dimple (squish) + whole-blob shift (bulge shares with sideways flow)
                Vector3 tgtC = colTarget[i] * (dentMax * squishS * 0.5f * w[i]) + meanPush * (dentMax * rigidS * 0.5f * w[i]);
                float cm = tgtC.magnitude;
                if (cm > colCap) tgtC *= colCap / cm;
                colCur[i] = Vector3.Lerp(colCur[i], tgtC, ease);
            }

            // ---- HARD non-penetration: the skin never lets the collider through. After the
            // soft layer, any vertex still inside a collider is projected EXACTLY onto its
            // surface (applied instantly, un-eased, so fast hands can't tunnel).
            float amp0 = Mathf.Max(0f, cfg.jiggle);
            float dbgCur = 0f;
            for (int iter = 0; iter < 2; iter++)
                for (int i = 0; i < n; i++)
                {
                    Vector3 p = baked[idx[i]] + offset[i] * amp0 + colCur[i];
                    for (int c = 0; c < ncol; c++)
                    {
                        if (!colCfg[c].enabled) continue;
                        bool isMesh = !string.IsNullOrEmpty(colCfg[c].mesh);
                        if (isMesh)
                        {
                            MeshColliderCloud cl = (clouds != null && c < clouds.Length) ? clouds[c] : null;
                            if (cl == null) continue;
                            Vector3 push;
                            if (cl.TryPush(p, out push)) { colCur[i] += push; p += push; }
                        }
                        else
                        {
                            Transform ct = colTr != null ? colTr[c] : null;
                            if (ct == null) continue;
                            Vector3 cp0 = proxy.InverseTransformPoint(ct.position);
                            float rad = colCfg[c].radius / Mathf.Max(0.0001f, proxy.lossyScale.x);
                            Vector3 cp = cp0;
                            if (colCfg[c].length > 0f)
                            {
                                Vector3 b2 = proxy.InverseTransformPoint(ct.position + ct.forward * colCfg[c].length);
                                cp = ClosestOnSegment(cp0, b2, p);
                            }
                            Vector3 d = p - cp; float dist = d.magnitude;
                            if (dist < rad && dist > 1e-6f)
                            { Vector3 push = d / dist * (rad - dist); colCur[i] += push; p += push; }
                        }
                    }
                    float c2 = colCur[i].sqrMagnitude; if (c2 > dbgCur) dbgCur = c2;
                }

            // pipeline diagnostics every ~2 s: raw contact -> after baseline -> after RBF -> final metres
            diagTimer += dt;
            if (diagTimer > 2f)
            {
                diagTimer = 0f;
                int cloudPts = 0;
                if (clouds != null)
                    for (int c = 0; c < clouds.Length; c++) if (clouds[c] != null) cloudPts += clouds[c].pts.Count;
                Debug.Log("[Wobble] dbg '" + cfg.name + "' raw=" + Mathf.Sqrt(dbgRaw).ToString("0.000")
                    + " eff=" + Mathf.Sqrt(dbgEff).ToString("0.000")
                    + " rbf=" + Mathf.Sqrt(dbgRbf).ToString("0.000")
                    + " curM=" + Mathf.Sqrt(dbgCur).ToString("0.000")
                    + " K=" + K + " cloud=" + cloudPts + " dentMax=" + dentMax.ToString("0.00"));
            }

            // ---- output: jiggle + collision + all the styled modes, written to every duplicate ----
            float amp = Mathf.Max(0f, cfg.jiggle);
            float liqLambda = Mathf.Lerp(0.02f, 0.25f, Mathf.Clamp01(cfg.liquidSize));
            float pulseHz = Mathf.Lerp(0.15f, 2.2f, Mathf.Clamp01(cfg.pulseRate));
            float pulseS = Mathf.Sin(simT * 2f * Mathf.PI * pulseHz);
            Vector3 stretchDir = Vector3.zero; float stretchAmt = 0f;
            if (cfg.stretch > 0.001f)
            {
                float spd = lastCentroidVel.magnitude;
                if (spd > 0.05f) { stretchDir = lastCentroidVel / spd; stretchAmt = Mathf.Min(spd * cfg.stretch * 0.12f, 0.5f); }
            }
            float turbF = Mathf.Lerp(60f, 8f, Mathf.Clamp01(cfg.turbSize));

            bool blewUp = false;
            for (int i = 0; i < n; i++)
            {
                int vi = idx[i];
                if (vi >= baked.Length) continue;
                Vector3 target = baked[vi];
                Vector3 nrm = normals[vi];

                Vector3 outOff = offset[i] * amp + colCur[i];

                if (cfg.clothRipple > 0.001f)
                    outOff += nrm * (clothH[i] * cfg.clothRipple * 1.5f * w[i]);
                if (cfg.jello > 0.001f)
                    outOff += jelloPos * (cfg.jello * 1.6f * jelloF[i] * w[i]);
                if (cfg.sway > 0.001f)
                    outOff += swayPos * (cfg.sway * 1.4f * w[i]);
                if (cfg.twistJiggle > 0.001f)
                    outOff += Vector3.Cross(localDown, target - lastCentroid) * (twist * cfg.twistJiggle * w[i]);
                if (cfg.pulse > 0.001f)
                    outOff += nrm * (pulseS * cfg.pulse * 0.008f * w[i]);
                if (stretchAmt > 0f)
                    outOff += stretchDir * (Vector3.Dot(target - lastCentroid, stretchDir) * stretchAmt * w[i]);
                if (cfg.turbulence > 0.001f)
                {
                    float t1 = Mathf.PerlinNoise(target.x * turbF + simT * 1.7f, target.y * turbF) - 0.5f;
                    outOff += nrm * (t1 * cfg.turbulence * 0.02f * w[i]);
                }
                for (int k = 0; k < waves.Count; k++)
                {
                    Wave wv = waves[k];
                    float r = (target - wv.pos).magnitude;
                    float phase = (r / liqLambda) - wv.t * 8f;
                    float env = Mathf.Exp(-wv.t * 2.2f) * Mathf.Exp(-r * 6f);
                    outOff += nrm * (Mathf.Sin(phase * Mathf.PI * 2f) * wv.amp * env * w[i]);
                }
                if (cfg.cellulite > 0.0001f && cellNoise != null)
                    outOff += nrm * (cellNoise[i] * cfg.cellulite * 0.01f * w[i]);

                float om = outOff.sqrMagnitude;
                if (float.IsNaN(om) || om > 1f) { outOff = Vector3.zero; blewUp = true; }

                int[] mem = members[i];
                for (int mIdx = 0; mIdx < mem.Length; mIdx++)
                    if (mem[mIdx] < disp.Length) disp[mem[mIdx]] += outOff;
            }
            if (blewUp)
            {
                Debug.LogWarning("[Wobble] region '" + cfg.name + "' destabilised (NaN/huge offset) — state reset");
                ResetState();
            }
        }

        static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a;
            float t = Vector3.Dot(p - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude);
            return a + ab * Mathf.Clamp01(t);
        }

        public void ResetState()
        {
            if (offset == null) return;
            for (int i = 0; i < offset.Length; i++)
            {
                offset[i] = Vector3.zero; vel[i] = Vector3.zero;
                if (clothH != null) { clothH[i] = 0f; clothV[i] = 0f; }
                if (colCur != null) { colCur[i] = Vector3.zero; colTarget[i] = Vector3.zero; colBase[i] = Vector3.zero; }
            }
            jelloPos = Vector3.zero; jelloVel = Vector3.zero;
            swayPos = Vector3.zero; swayVel = Vector3.zero; twist = 0f; twistVel = 0f;
            prevPushMag = 0f;
            primed = false; waves.Clear();
        }
    }
}
