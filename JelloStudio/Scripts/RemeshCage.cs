using System.Collections.Generic;
using UnityEngine;

namespace JelloStudio
{
    // Simulation CAGE: a duplicate mesh of ALL enabled regions, isotropically REMESHED
    // to a uniform edge length so the solver never sees the original mesh's mixed
    // 0.4mm-8mm topology. The cage is "skinned" by barycentric binding to the original
    // surface, physics runs on the cage, and the deformation field is projected back
    // onto the original vertices (barycentric again). Rendered detail is untouched -
    // only the PHYSICS sees the clean uniform mesh.
    public class RemeshCage
    {
        public Vector3[] simRest;
        public int[] simTris;
        public float[] simWeight;
        public Vector3[] simBaked, simNormals, simDisp;
        public int SimVertCount { get { return simRest != null ? simRest.Length : 0; } }
        public float usedEdge;                                  // edge length actually used (auto-capped)

        int[] bindA, bindB, bindC; float[] barA, barB, barC;    // cage vert -> original tri (skin binding)
        int[] pV, pA, pB, pC; float[] pwA, pwB, pwC;            // original vert -> cage tri (projection)
        int[][] cageNbr; Vector3[] tmpDisp;                     // cage graph adjacency + scratch for disp smoothing

        public string logTag = "[Cage]";
        public volatile bool buildDone;                          // set by the worker thread
        public volatile bool buildOk;
        public volatile string stage = "queued";                 // progress for the MAIN thread to log
        public volatile string note = "";                        // error/warning detail (logged by main)
        public int gateUsed, gateFallback;                       // normal-gate stats (logged by main)

        // MAIN THREAD ONLY: region lists are mutated by painting, so the paint union
        // must be flattened before the worker thread starts
        public static float[] UnionWeights(int mv, List<SquishRegion> regions, int[] weldOf)
        {
            float[] wRep = new float[mv];
            bool anyW = false;
            for (int r = 0; r < regions.Count; r++)
            {
                SquishRegion reg = regions[r];
                if (!reg.enabled) continue;
                for (int i = 0; i < reg.vertIndex.Count; i++)
                {
                    int vi = reg.vertIndex[i];
                    if (vi < 0 || vi >= mv || reg.weight[i] <= 0f) continue;
                    int rp = weldOf[vi];
                    if (reg.weight[i] > wRep[rp]) wRep[rp] = reg.weight[i];
                    anyW = true;
                }
            }
            return anyW ? wRep : null;
        }

        // THREAD-SAFE: math structs and Debug.Log only, no Unity objects.
        public bool Build(Vector3[] mvp, int[] meshTris, int[] weldOf, List<int>[] weldMembers,
                          float[] wRep, Vector3[] mNrm, float targetEdge, int passes)
        {
            int mv = mvp.Length;
            if (wRep == null) return false;
            stage = "submesh";

            // submesh over weld reps: any triangle touching paint (cage extends one ring
            // past the paint boundary so the glue zone is part of the cage)
            int[] repLocal = new int[mv]; for (int i = 0; i < mv; i++) repLocal[i] = -1;
            List<Vector3> P = new List<Vector3>(); List<float> W = new List<float>();
            List<int> repOf = new List<int>();
            List<int> T = new List<int>();
            for (int t = 0; t < meshTris.Length; t += 3)
            {
                int a = weldOf[meshTris[t]], b = weldOf[meshTris[t + 1]], c = weldOf[meshTris[t + 2]];
                if (a == b || b == c || a == c) continue;
                if (wRep[a] <= 0f && wRep[b] <= 0f && wRep[c] <= 0f) continue;
                T.Add(Local(a, repLocal, P, W, repOf, mvp, wRep, weldMembers));
                T.Add(Local(b, repLocal, P, W, repOf, mvp, wRep, weldMembers));
                T.Add(Local(c, repLocal, P, W, repOf, mvp, wRep, weldMembers));
            }
            if (T.Count < 3) return false;
            stage = "grid (" + (T.Count / 3) + " tris)";

            // frozen snapshot of the ORIGINAL region surface (reprojection target)
            Vector3[] oP = P.ToArray(); float[] oW = W.ToArray();
            int[] oT = T.ToArray(); int[] oRep = repOf.ToArray();

            // vert-count safety cap: expected verts ~ 1.16 * area / L^2
            float area = 0f;
            for (int t = 0; t < oT.Length; t += 3)
                area += Vector3.Cross(oP[oT[t + 1]] - oP[oT[t]], oP[oT[t + 2]] - oP[oT[t]]).magnitude * 0.5f;
            float L = Mathf.Max(targetEdge, 0.0005f);
            float est = 1.16f * area / (L * L);
            if (est > 12000f) L = Mathf.Sqrt(1.16f * area / 12000f);
            usedEdge = L;

            // grid cell ~ the ORIGINAL submesh's typical edge (equilateral estimate) —
            // sizing it from the target L made every query a near-full scan at coarse L
            float typEdge = Mathf.Sqrt(area * 2.3094f / Mathf.Max(1, oT.Length / 3));
            TriGrid grid = new TriGrid(oP, oT, Mathf.Clamp(typEdge * 2f, 0.002f, 0.05f));

            // isotropic remesh (Botsch-Kobbelt style): split long, collapse short,
            // flip for valence, relax + reproject onto the original surface
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            const long BUDGET = 9000;
            // hard ceiling on cage size: a correct build lands near `est` verts, so this
            // only trips on a genuine runaway — it guarantees the loop can never hang
            int vertCeiling = Mathf.Clamp((int)(est * 2f) + 4000, 16000, 120000);
            bool budgetHit = false;
            for (int pass = 0; pass < passes && !budgetHit; pass++)
            {
                stage = "remesh pass " + (pass + 1) + "/" + passes;
                // splits and collapses each need several sweeps to converge when the
                // target is far from the source edge lengths (one sweep only touches
                // each triangle once) — iterate until nothing changes
                for (int i = 0; i < 8; i++)
                {
                    if (sw.ElapsedMilliseconds > BUDGET) { budgetHit = true; break; }
                    if (P.Count > vertCeiling) { budgetHit = true; note = "vert ceiling " + vertCeiling + " hit — geometry likely degenerate"; break; }
                    if (SplitLong(P, W, T, L * 1.334f, sw, BUDGET) == 0) break;
                }
                for (int i = 0; i < 24 && !budgetHit; i++)
                {
                    if (sw.ElapsedMilliseconds > BUDGET) { budgetHit = true; break; }
                    if (CollapseShort(P, W, T, L * 0.8f, L * 1.334f, sw, BUDGET) == 0) break;
                }
                if (sw.ElapsedMilliseconds > BUDGET) budgetHit = true;
                FlipEdges(P, T);
                SmoothProject(P, T, grid);
            }
            if (budgetHit)
                note = "remesh time budget hit — using partially remeshed cage";
            CompactUnused(P, W, T);
            if (P.Count < 4 || T.Count < 3) return false;
            stage = "binding (" + P.Count + " verts)";

            int ns = P.Count;
            simRest = P.ToArray(); simTris = T.ToArray();
            simWeight = new float[ns];
            bindA = new int[ns]; bindB = new int[ns]; bindC = new int[ns];
            barA = new float[ns]; barB = new float[ns]; barC = new float[ns];
            for (int i = 0; i < ns; i++)
            {
                int bt; Vector3 cp, bar;
                if (!grid.Nearest(simRest[i], out bt, out cp, out bar)) return false;
                simRest[i] = cp;                                 // sit exactly on the surface
                bindA[i] = oRep[oT[bt]]; bindB[i] = oRep[oT[bt + 1]]; bindC[i] = oRep[oT[bt + 2]];
                barA[i] = bar.x; barB[i] = bar.y; barC[i] = bar.z;
                simWeight[i] = oW[oT[bt]] * bar.x + oW[oT[bt + 1]] * bar.y + oW[oT[bt + 2]] * bar.z;
            }

            stage = "projection";
            // CAGE FACE NORMALS for the valley gate: raw winding from the remesher is not
            // guaranteed consistent, so canonicalize each face normal against the render
            // normals its verts are bound to (skin binding above).
            int nTri = simTris.Length / 3;
            Vector3[] triN = new Vector3[nTri];
            if (mNrm != null)
                for (int t = 0; t < nTri; t++)
                {
                    int a = simTris[t * 3], b = simTris[t * 3 + 1], c2 = simTris[t * 3 + 2];
                    Vector3 fn = Vector3.Cross(simRest[b] - simRest[a], simRest[c2] - simRest[a]);
                    float fm = fn.magnitude;
                    fn = fm > 1e-12f ? fn / fm : Vector3.up;
                    Vector3 refN = mNrm[bindA[a]] + mNrm[bindA[b]] + mNrm[bindA[c2]];
                    if (Vector3.Dot(fn, refN) < 0f) fn = fn * -1f;
                    triN[t] = fn;
                }
            // projection tables: every painted original vertex rides the nearest cage tri
            // that FACES THE SAME WAY (dot >= 0.35). Across a valley/crack the two walls
            // face opposite directions, so Euclidean-nearest-but-wrong-wall triangles are
            // rejected and each wall rides its OWN side. Falls back to plain nearest when
            // nothing passes (valley floor, degenerate normals) — never worse than before.
            TriGrid sgrid = new TriGrid(simRest, simTris, Mathf.Max(L * 2f, 0.01f));
            sgrid.triNormals = mNrm != null ? triN : null;
            gateUsed = 0; gateFallback = 0;
            List<int> jV = new List<int>(), jA = new List<int>(), jB = new List<int>(), jC = new List<int>();
            List<float> jwA = new List<float>(), jwB = new List<float>(), jwC = new List<float>();
            for (int g = 0; g < weldMembers.Length; g++)
            {
                if (wRep[g] <= 0f) continue;
                List<int> mem = weldMembers[g];
                if (mem == null || mem.Count == 0) continue;
                Vector3 nq = Vector3.zero;
                if (mNrm != null)
                {
                    for (int m = 0; m < mem.Count; m++) nq += mNrm[mem[m]];
                    float nm2 = nq.magnitude;
                    nq = nm2 > 1e-9f ? nq / nm2 : Vector3.zero;
                }
                int bt; Vector3 cp, bar; bool viaGate;
                if (!sgrid.NearestGated(mvp[mem[0]], nq, 0.35f, out bt, out cp, out bar, out viaGate)) continue;
                if (viaGate) gateUsed++; else gateFallback++;
                for (int m = 0; m < mem.Count; m++)
                {
                    jV.Add(mem[m]);
                    jA.Add(simTris[bt]); jB.Add(simTris[bt + 1]); jC.Add(simTris[bt + 2]);
                    jwA.Add(bar.x); jwB.Add(bar.y); jwC.Add(bar.z);
                }
            }
            pV = jV.ToArray(); pA = jA.ToArray(); pB = jB.ToArray(); pC = jC.ToArray();
            pwA = jwA.ToArray(); pwB = jwB.ToArray(); pwC = jwC.ToArray();

            simBaked = new Vector3[ns]; simNormals = new Vector3[ns]; simDisp = new Vector3[ns];
            // cage graph adjacency for the pre-projection displacement smoothers
            {
                List<int>[] adj = new List<int>[ns];
                for (int i = 0; i < ns; i++) adj[i] = new List<int>(6);
                for (int t = 0; t < simTris.Length; t += 3)
                {
                    int a = simTris[t], b = simTris[t + 1], cc = simTris[t + 2];
                    if (!adj[a].Contains(b)) { adj[a].Add(b); adj[b].Add(a); }
                    if (!adj[b].Contains(cc)) { adj[b].Add(cc); adj[cc].Add(b); }
                    if (!adj[a].Contains(cc)) { adj[a].Add(cc); adj[cc].Add(a); }
                }
                cageNbr = new int[ns][];
                for (int i = 0; i < ns; i++) cageNbr[i] = adj[i].ToArray();
            }
            stage = "done";
            return true;
        }

        // -------- per frame --------
        public void InterpBaked(Vector3[] baked, Vector3[] normals)
        {
            for (int i = 0; i < simBaked.Length; i++)
            {
                simBaked[i] = baked[bindA[i]] * barA[i] + baked[bindB[i]] * barB[i] + baked[bindC[i]] * barC[i];
                Vector3 nn = normals[bindA[i]] * barA[i] + normals[bindB[i]] * barB[i] + normals[bindC[i]] * barC[i];
                float m = nn.magnitude;
                simNormals[i] = m > 1e-9f ? nn / m : Vector3.up;
            }
        }

        public void Project(Vector3[] disp)
        {
            for (int k = 0; k < pV.Length; k++)
                disp[pV[k]] += simDisp[pA[k]] * pwA[k] + simDisp[pB[k]] * pwB[k] + simDisp[pC[k]] * pwC[k];
        }

        // Smooth the CAGE displacement field before it is projected onto the mesh.
        // taubinPasses: shrink-free peak/sharp-edge removal (keeps overall shape).
        // avgPasses: plain diffusion — widens each cage node's influence = the
        // "projection averaging range". Both run on the uniform cage graph, so they
        // are stable and live-adjustable with no rebuild.
        public void SmoothDisp(int taubinPasses, int avgPasses)
        {
            if (cageNbr == null || simDisp == null) return;
            int n = simDisp.Length;
            if (tmpDisp == null || tmpDisp.Length != n) tmpDisp = new Vector3[n];
            for (int pass = 0; pass < taubinPasses; pass++)
            {
                float lam = (pass & 1) == 0 ? 0.5f : -0.53f;
                for (int i = 0; i < n; i++)
                {
                    int[] nb = cageNbr[i];
                    if (nb.Length < 2) { tmpDisp[i] = simDisp[i]; continue; }
                    Vector3 avg = Vector3.zero;
                    for (int j = 0; j < nb.Length; j++) avg += simDisp[nb[j]];
                    avg /= nb.Length;
                    tmpDisp[i] = simDisp[i] + (avg - simDisp[i]) * lam;
                }
                Vector3[] sw = simDisp; simDisp = tmpDisp; tmpDisp = sw;
            }
            for (int pass = 0; pass < avgPasses; pass++)
            {
                for (int i = 0; i < n; i++)
                {
                    int[] nb = cageNbr[i];
                    if (nb.Length < 2) { tmpDisp[i] = simDisp[i]; continue; }
                    Vector3 avg = Vector3.zero;
                    for (int j = 0; j < nb.Length; j++) avg += simDisp[nb[j]];
                    avg /= nb.Length;
                    tmpDisp[i] = simDisp[i] + (avg - simDisp[i]) * 0.5f;
                }
                Vector3[] sw = simDisp; simDisp = tmpDisp; tmpDisp = sw;
            }
        }

        // -------- contact boost + slap (2nd-level squish) --------
        // Runs on the MAIN thread each frame, after SmoothDisp and before Project, so the
        // smoothers cannot dilute it. Boost: residual penetration of the SMOOTHED cage is
        // pushed back out by depth*strength (the smoothing is exactly what re-sinks the
        // skin into the collider — this restores the sharp local dent). Slap: a fast rise
        // in penetration (approach speed) injects a decaying outward impulse, which the
        // spread + the jello's own wobble turn into a visible smack. Both fields are
        // diffused over the cage graph (shard law: no raw per-vertex steps).
        float[] cbPrev, cbImp; Vector3[] cbDir, cbA, cbB;
        public void ContactBoost(SquishSim sim, Transform proxy, float boost, int spread, float maxDepth,
                                 float slapSens, float slapPower, float dt)
        {
            int n = simDisp.Length;
            if (cbPrev == null || cbPrev.Length != n)
            { cbPrev = new float[n]; cbImp = new float[n]; cbDir = new Vector3[n]; cbA = new Vector3[n]; cbB = new Vector3[n]; }
            float decay = Mathf.Max(0f, 1f - dt * 5f);        // slap impulse fades over ~0.2 s
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                Vector3 pnt = simBaked[i] + simDisp[i];
                Vector3 dir;
                float d = sim.PenetrationAt(pnt, proxy, out dir);
                float rate = dt > 1e-5f ? (d - cbPrev[i]) / dt : 0f;
                cbPrev[i] = d;
                if (d > 0.0001f) cbDir[i] = dir;              // remember last push direction for the decay tail
                float imp = cbImp[i] * decay;
                if (slapPower > 0.001f && rate > slapSens)
                    imp = Mathf.Max(imp, (rate - slapSens) * slapPower * 0.05f);   // 1 m/s over threshold ≈ 5 cm × power
                cbImp[i] = imp;
                Vector3 e = Vector3.zero;
                if (d > 0.0001f && boost > 0.001f) e = cbDir[i] * Mathf.Min(d * boost, maxDepth);
                if (imp > 0.0005f) e += cbDir[i] * Mathf.Min(imp, maxDepth);
                cbA[i] = e;
                if (e.sqrMagnitude > 1e-12f) any = true;
            }
            if (!any) return;
            for (int pass = 0; pass < spread; pass++)
            {
                for (int i = 0; i < n; i++)
                {
                    int[] nb = cageNbr[i];
                    if (nb.Length < 2) { cbB[i] = cbA[i]; continue; }
                    Vector3 avg = Vector3.zero;
                    for (int j = 0; j < nb.Length; j++) avg += cbA[nb[j]];
                    avg /= nb.Length;
                    cbB[i] = cbA[i] + (avg - cbA[i]) * 0.5f;
                }
                Vector3[] sw = cbA; cbA = cbB; cbB = sw;
            }
            for (int i = 0; i < n; i++) simDisp[i] += cbA[i];
        }

        // -------- viz --------
        public Mesh MakeVizMesh()
        {
            Mesh m = new Mesh();
            if (simRest.Length > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.vertices = simRest;
            Color[] cols = new Color[simRest.Length];
            for (int i = 0; i < cols.Length; i++)
                cols[i] = Color.Lerp(new Color(0.15f, 0.3f, 1f), new Color(1f, 0.25f, 0.15f), simWeight[i]);
            m.colors = cols;
            m.triangles = simTris;
            m.RecalculateNormals(); m.RecalculateBounds(); m.MarkDynamic();
            return m;
        }
        static readonly List<Vector3> vizScratch = new List<Vector3>();
        public void UpdateViz(Mesh m)
        {
            vizScratch.Clear();
            for (int i = 0; i < simBaked.Length; i++)
                vizScratch.Add(simBaked[i] + simDisp[i] + simNormals[i] * 0.0015f);
            m.SetVertices(vizScratch);
            m.RecalculateNormals(); m.RecalculateBounds();
        }

        // ==================== remesh operators ====================
        // rep is a WELD-GROUP ORDINAL, not a render vertex index. mvp/baked are
        // render-indexed, so we must resolve the group's representative RENDER vertex
        // (weldMembers[rep][0]) before sampling position — using the ordinal directly
        // scrambles positions the moment welding shifts ordinals below render indices,
        // which manufactures giant fake edges and makes the remesher run away.
        static int Local(int rep, int[] repLocal, List<Vector3> P, List<float> W, List<int> repOf, Vector3[] mvp, float[] wRep, List<int>[] weldMembers)
        {
            int l = repLocal[rep];
            if (l >= 0) return l;
            l = P.Count; repLocal[rep] = l;
            int rv = (weldMembers[rep] != null && weldMembers[rep].Count > 0) ? weldMembers[rep][0] : rep;
            P.Add(mvp[rv]); W.Add(wRep[rep]); repOf.Add(rv);   // repOf stores a RENDER index (for baked[] binding)
            return l;
        }
        static float MaxEdge(Vector3[] P, int[] T)
        {
            float mx = 0.001f;
            for (int t = 0; t < T.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    float d = (P[T[t + e]] - P[T[t + (e + 1) % 3]]).magnitude;
                    if (d > mx) mx = d;
                }
            return mx;
        }
        static long EKey(int a, int b) { return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a; }
        static Dictionary<long, List<int>> EdgeTris(List<int> T)
        {
            Dictionary<long, List<int>> e2t = new Dictionary<long, List<int>>();
            for (int t = 0; t < T.Count; t += 3)
            {
                if (T[t] < 0) continue;
                for (int e = 0; e < 3; e++)
                {
                    long k = EKey(T[t + e], T[t + (e + 1) % 3]);
                    List<int> l;
                    if (!e2t.TryGetValue(k, out l)) { l = new List<int>(2); e2t[k] = l; }
                    l.Add(t);
                }
            }
            return e2t;
        }
        static void Compact(List<int> T)
        {
            int w = 0;
            for (int t = 0; t < T.Count; t += 3)
            {
                if (T[t] < 0) continue;
                T[w] = T[t]; T[w + 1] = T[t + 1]; T[w + 2] = T[t + 2]; w += 3;
            }
            T.RemoveRange(w, T.Count - w);
        }
        static void CompactUnused(List<Vector3> P, List<float> W, List<int> T)
        {
            int n = P.Count;
            int[] map = new int[n]; for (int i = 0; i < n; i++) map[i] = -1;
            List<Vector3> nP = new List<Vector3>(n); List<float> nW = new List<float>(n);
            for (int t = 0; t < T.Count; t++)
            {
                int v = T[t];
                if (map[v] < 0) { map[v] = nP.Count; nP.Add(P[v]); nW.Add(W[v]); }
                T[t] = map[v];
            }
            P.Clear(); P.AddRange(nP);
            W.Clear(); W.AddRange(nW);
        }

        static int SplitLong(List<Vector3> P, List<float> W, List<int> T, float hi, System.Diagnostics.Stopwatch sw, long budget)
        {
            int changed = 0;
            Dictionary<long, List<int>> e2t = EdgeTris(T);
            List<long> longs = new List<long>();
            foreach (KeyValuePair<long, List<int>> kv in e2t)
            {
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xFFFFFFFFL);
                if ((P[a] - P[b]).magnitude > hi) longs.Add(kv.Key);
            }
            HashSet<int> touched = new HashSet<int>();
            for (int i = 0; i < longs.Count; i++)
            {
                if ((i & 1023) == 0 && sw.ElapsedMilliseconds > budget) break;
                int u = (int)(longs[i] >> 32), v = (int)(longs[i] & 0xFFFFFFFFL);
                List<int> ts = e2t[longs[i]];
                bool bad = false;
                for (int j = 0; j < ts.Count; j++) if (T[ts[j]] < 0 || touched.Contains(ts[j])) { bad = true; break; }
                if (bad) continue;
                int mid = P.Count;
                P.Add((P[u] + P[v]) * 0.5f); W.Add((W[u] + W[v]) * 0.5f);
                changed++;
                for (int j = 0; j < ts.Count; j++)
                {
                    int t = ts[j];
                    touched.Add(t);
                    int i0 = T[t], i1 = T[t + 1], i2 = T[t + 2];
                    T[t] = -1;
                    int[] tri = { i0, i1, i2 };
                    for (int e = 0; e < 3; e++)
                    {
                        int a = tri[e], b = tri[(e + 1) % 3], c = tri[(e + 2) % 3];
                        if ((a == u && b == v) || (a == v && b == u))
                        {
                            T.Add(a); T.Add(mid); T.Add(c);
                            T.Add(mid); T.Add(b); T.Add(c);
                            break;
                        }
                    }
                }
            }
            Compact(T);
            return changed;
        }

        static int CollapseShort(List<Vector3> P, List<float> W, List<int> T, float lo, float hi, System.Diagnostics.Stopwatch sw, long budget)
        {
            int changed = 0;
            Dictionary<long, List<int>> e2t = EdgeTris(T);
            int n = P.Count;
            bool[] boundary = new bool[n];
            List<int>[] vadj = new List<int>[n];
            for (int i = 0; i < n; i++) vadj[i] = new List<int>(8);
            foreach (KeyValuePair<long, List<int>> kv in e2t)
            {
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xFFFFFFFFL);
                vadj[a].Add(b); vadj[b].Add(a);
                if (kv.Value.Count < 2) { boundary[a] = true; boundary[b] = true; }
            }
            List<int>[] vtris = new List<int>[n];
            for (int i = 0; i < n; i++) vtris[i] = new List<int>(8);
            for (int t = 0; t < T.Count; t += 3)
            {
                if (T[t] < 0) continue;
                vtris[T[t]].Add(t); vtris[T[t + 1]].Add(t); vtris[T[t + 2]].Add(t);
            }
            bool[] touched = new bool[n];
            // shortest edges first: they are the instability we exist to remove, and
            // collapsing them first frees their neighbourhoods for the next sweep
            List<KeyValuePair<float, long>> shorts = new List<KeyValuePair<float, long>>();
            foreach (KeyValuePair<long, List<int>> kv in e2t)
            {
                int a2 = (int)(kv.Key >> 32), b2 = (int)(kv.Key & 0xFFFFFFFFL);
                float len = (P[a2] - P[b2]).magnitude;
                if (len < lo && !boundary[a2] && !boundary[b2]) shorts.Add(new KeyValuePair<float, long>(len, kv.Key));
            }
            shorts.Sort(CmpLen);
            for (int si = 0; si < shorts.Count; si++)
            {
                if ((si & 1023) == 0 && sw.ElapsedMilliseconds > budget) break;
                int u = (int)(shorts[si].Value >> 32), v = (int)(shorts[si].Value & 0xFFFFFFFFL);
                if (touched[u] || touched[v]) continue;
                Vector3 mid = (P[u] + P[v]) * 0.5f;
                bool ok = true;
                for (int j = 0; j < vadj[u].Count && ok; j++) { int q = vadj[u][j]; if (q != v && (mid - P[q]).magnitude > hi) ok = false; }
                for (int j = 0; j < vadj[v].Count && ok; j++) { int q = vadj[v][j]; if (q != u && (mid - P[q]).magnitude > hi) ok = false; }
                if (!ok) continue;
                P[u] = mid; W[u] = (W[u] + W[v]) * 0.5f;
                for (int j = 0; j < vtris[v].Count; j++)
                {
                    int t = vtris[v][j];
                    if (T[t] < 0) continue;
                    bool hasU = (T[t] == u || T[t + 1] == u || T[t + 2] == u);
                    if (hasU) { T[t] = -1; continue; }
                    for (int e = 0; e < 3; e++) if (T[t + e] == v) T[t + e] = u;
                    vtris[u].Add(t);
                }
                touched[u] = true; touched[v] = true;
                for (int j = 0; j < vadj[u].Count; j++) touched[vadj[u][j]] = true;
                for (int j = 0; j < vadj[v].Count; j++) touched[vadj[v][j]] = true;
                changed++;
            }
            Compact(T);
            return changed;
        }
        static int CmpLen(KeyValuePair<float, long> a, KeyValuePair<float, long> b) { return a.Key.CompareTo(b.Key); }

        static void FlipEdges(List<Vector3> P, List<int> T)
        {
            Dictionary<long, List<int>> e2t = EdgeTris(T);
            int n = P.Count;
            int[] val = new int[n];
            bool[] boundary = new bool[n];
            foreach (KeyValuePair<long, List<int>> kv in e2t)
            {
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xFFFFFFFFL);
                val[a]++; val[b]++;
                if (kv.Value.Count < 2) { boundary[a] = true; boundary[b] = true; }
            }
            HashSet<int> touched = new HashSet<int>();
            foreach (KeyValuePair<long, List<int>> kv in e2t)
            {
                if (kv.Value.Count != 2) continue;
                int t1 = kv.Value[0], t2 = kv.Value[1];
                if (T[t1] < 0 || T[t2] < 0 || touched.Contains(t1) || touched.Contains(t2)) continue;
                int u = (int)(kv.Key >> 32), v = (int)(kv.Key & 0xFFFFFFFFL);
                int a = Third(T, t1, u, v), b = Third(T, t2, u, v);
                if (a < 0 || b < 0 || a == b) continue;
                if (e2t.ContainsKey(EKey(a, b))) continue;
                if (val[u] <= 3 || val[v] <= 3) continue;
                int before = Dev(val[u], boundary[u]) + Dev(val[v], boundary[v]) + Dev(val[a], boundary[a]) + Dev(val[b], boundary[b]);
                int after = Dev(val[u] - 1, boundary[u]) + Dev(val[v] - 1, boundary[v]) + Dev(val[a] + 1, boundary[a]) + Dev(val[b] + 1, boundary[b]);
                if (after >= before) continue;
                int du = u, dv = v;
                bool fwd = false;
                for (int e = 0; e < 3 && !fwd; e++)
                    if (T[t1 + e] == u && T[t1 + (e + 1) % 3] == v) fwd = true;
                if (!fwd) { du = v; dv = u; }
                // t1=(du,dv,a), t2=(dv,du,b)  ->  (du,b,a) and (dv,a,b)
                T[t1] = du; T[t1 + 1] = b; T[t1 + 2] = a;
                T[t2] = dv; T[t2 + 1] = a; T[t2 + 2] = b;
                val[du]--; val[dv]--; val[a]++; val[b]++;
                touched.Add(t1); touched.Add(t2);
            }
        }
        static int Third(List<int> T, int t, int u, int v)
        {
            for (int e = 0; e < 3; e++) { int x = T[t + e]; if (x != u && x != v) return x; }
            return -1;
        }
        static int Dev(int valence, bool bnd) { int d = valence - (bnd ? 4 : 6); return d < 0 ? -d : d; }

        static void SmoothProject(List<Vector3> P, List<int> T, TriGrid grid)
        {
            Dictionary<long, List<int>> e2t = EdgeTris(T);
            int n = P.Count;
            bool[] boundary = new bool[n];
            Vector3[] cen = new Vector3[n]; int[] cnt = new int[n];
            foreach (KeyValuePair<long, List<int>> kv in e2t)
            {
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xFFFFFFFFL);
                cen[a] += P[b]; cnt[a]++; cen[b] += P[a]; cnt[b]++;
                if (kv.Value.Count < 2) { boundary[a] = true; boundary[b] = true; }
            }
            for (int i = 0; i < n; i++)
            {
                if (boundary[i] || cnt[i] < 3) continue;
                Vector3 target = P[i] + (cen[i] / cnt[i] - P[i]) * 0.5f;
                int bt; Vector3 cp, bar;
                P[i] = grid.Nearest(target, out bt, out cp, out bar) ? cp : target;
            }
        }

        // ==================== triangle spatial hash ====================
        class TriGrid
        {
            readonly Vector3[] P; readonly int[] T; readonly float cell;
            readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
            public TriGrid(Vector3[] p, int[] t, float cellSize)
            {
                P = p; T = t; cell = Mathf.Max(cellSize, 0.001f);
                for (int i = 0; i < T.Length; i += 3)
                {
                    Vector3 a = P[T[i]], b = P[T[i + 1]], c = P[T[i + 2]];
                    Vector3 mn = Vector3.Min(a, Vector3.Min(b, c)), mx = Vector3.Max(a, Vector3.Max(b, c));
                    int x0 = Mathf.FloorToInt(mn.x / cell), x1 = Mathf.FloorToInt(mx.x / cell);
                    int y0 = Mathf.FloorToInt(mn.y / cell), y1 = Mathf.FloorToInt(mx.y / cell);
                    int z0 = Mathf.FloorToInt(mn.z / cell), z1 = Mathf.FloorToInt(mx.z / cell);
                    if ((long)x1 - x0 > 96 || (long)y1 - y0 > 96 || (long)z1 - z0 > 96) continue;   // degenerate/NaN
                    for (int x = x0; x <= x1; x++)
                        for (int y = y0; y <= y1; y++)
                            for (int z = z0; z <= z1; z++)
                            {
                                long k = CKey(x, y, z);
                                List<int> l;
                                if (!cells.TryGetValue(k, out l)) { l = new List<int>(4); cells[k] = l; }
                                l.Add(i);
                            }
                }
            }
            static long CKey(int x, int y, int z)
            { return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF); }

            public Vector3[] triNormals;   // optional, indexed by tri/3 — enables the valley gate

            // Nearest with a FACING gate: only triangles with dot(nq, faceNormal) >= tau
            // are eligible; tracks the plain nearest as a fallback so a query never fails
            // harder than the ungated version. viaGate reports which path won.
            public bool NearestGated(Vector3 q, Vector3 nq, float tau, out int tri, out Vector3 cp, out Vector3 bar, out bool viaGate)
            {
                viaGate = false;
                if (triNormals == null || nq.sqrMagnitude < 0.5f)
                    return Nearest(q, out tri, out cp, out bar);
                tri = -1; cp = q; bar = new Vector3(1f, 0f, 0f);
                int triAny = -1; Vector3 cpAny = q;
                float best = float.MaxValue, bestAny = float.MaxValue;
                int cx = Mathf.FloorToInt(q.x / cell), cy = Mathf.FloorToInt(q.y / cell), cz = Mathf.FloorToInt(q.z / cell);
                for (int ring = 0; ring <= 24; ring++)
                {
                    for (int dx = -ring; dx <= ring; dx++)
                        for (int dy = -ring; dy <= ring; dy++)
                            for (int dz = -ring; dz <= ring; dz++)
                            {
                                int m = Mathf.Max(Mathf.Abs(dx), Mathf.Max(Mathf.Abs(dy), Mathf.Abs(dz)));
                                if (m != ring) continue;
                                List<int> l;
                                if (!cells.TryGetValue(CKey(cx + dx, cy + dy, cz + dz), out l)) continue;
                                for (int j = 0; j < l.Count; j++)
                                {
                                    int t = l[j];
                                    Vector3 c2 = ClosestPointTriangle(q, P[T[t]], P[T[t + 1]], P[T[t + 2]]);
                                    float d = (q - c2).sqrMagnitude;
                                    if (d < bestAny) { bestAny = d; triAny = t; cpAny = c2; }
                                    if (d < best && Vector3.Dot(nq, triNormals[t / 3]) >= tau)
                                    { best = d; tri = t; cp = c2; }
                                }
                            }
                    // stop only once the GATED best is provably nearest — stopping on the
                    // ungated best would end the scan before a same-side triangle is found
                    if (tri >= 0 && best <= (ring * cell) * (ring * cell)) break;
                }
                if (tri >= 0) { viaGate = true; bar = Bary(cp, P[T[tri]], P[T[tri + 1]], P[T[tri + 2]]); return true; }
                if (triAny < 0) return false;
                tri = triAny; cp = cpAny;
                bar = Bary(cp, P[T[tri]], P[T[tri + 1]], P[T[tri + 2]]);
                return true;   // fallback: plain nearest (old behaviour)
            }

            public bool Nearest(Vector3 q, out int tri, out Vector3 cp, out Vector3 bar)
            {
                tri = -1; cp = q; bar = new Vector3(1f, 0f, 0f);
                float best = float.MaxValue;
                int cx = Mathf.FloorToInt(q.x / cell), cy = Mathf.FloorToInt(q.y / cell), cz = Mathf.FloorToInt(q.z / cell);
                for (int ring = 0; ring <= 24; ring++)
                {
                    for (int dx = -ring; dx <= ring; dx++)
                        for (int dy = -ring; dy <= ring; dy++)
                            for (int dz = -ring; dz <= ring; dz++)
                            {
                                int m = Mathf.Max(Mathf.Abs(dx), Mathf.Max(Mathf.Abs(dy), Mathf.Abs(dz)));
                                if (m != ring) continue;
                                List<int> l;
                                if (!cells.TryGetValue(CKey(cx + dx, cy + dy, cz + dz), out l)) continue;
                                for (int j = 0; j < l.Count; j++) Test(q, l[j], ref tri, ref cp, ref best);
                            }
                    if (tri >= 0 && best <= (ring * cell) * (ring * cell)) break;
                }
                if (tri < 0) return false;   // nothing within 24 rings — caller handles it
                bar = Bary(cp, P[T[tri]], P[T[tri + 1]], P[T[tri + 2]]);
                return true;
            }
            void Test(Vector3 q, int t, ref int tri, ref Vector3 cp, ref float best)
            {
                Vector3 c2 = ClosestPointTriangle(q, P[T[t]], P[T[t + 1]], P[T[t + 2]]);
                float d = (q - c2).sqrMagnitude;
                if (d < best) { best = d; tri = t; cp = c2; }
            }

            static Vector3 Bary(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
            {
                Vector3 v0 = b - a, v1 = c - a, v2 = p - a;
                float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
                float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
                float den = d00 * d11 - d01 * d01;
                if (den < 1e-12f) return new Vector3(1f, 0f, 0f);
                float v = Mathf.Clamp01((d11 * d20 - d01 * d21) / den);
                float w = Mathf.Clamp01((d00 * d21 - d01 * d20) / den);
                float u = Mathf.Clamp01(1f - v - w);
                float s = u + v + w;
                return s < 1e-9f ? new Vector3(1f, 0f, 0f) : new Vector3(u / s, v / s, w / s);
            }
            // Ericson, Real-Time Collision Detection 5.1.5
            static Vector3 ClosestPointTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
            {
                Vector3 ab = b - a, ac = c - a, ap = p - a;
                float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
                if (d1 <= 0f && d2 <= 0f) return a;
                Vector3 bp = p - b;
                float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
                if (d3 >= 0f && d4 <= d3) return b;
                float vc = d1 * d4 - d3 * d2;
                if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
                Vector3 cp2 = p - c;
                float d5 = Vector3.Dot(ab, cp2), d6 = Vector3.Dot(ac, cp2);
                if (d6 >= 0f && d5 <= d6) return c;
                float vb = d5 * d2 - d1 * d6;
                if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
                float va = d3 * d6 - d5 * d4;
                if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
                    return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
                float den = 1f / (va + vb + vc);
                return a + ab * (vb * den) + ac * (vc * den);
            }
        }
    }
}
