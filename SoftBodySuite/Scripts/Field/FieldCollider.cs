using System.Collections.Generic;
using UnityEngine;

namespace SoftBodySuite.Field
{
    // Narrowphase for the whole Field stage. Everything in here is PROXY-LOCAL and pure
    // maths: no Transform, no Mesh, no UnityEngine object of any kind is touched, so the
    // whole class can move to a worker thread unchanged. The main thread captures the
    // collider primitives once per frame (mirroring SquishSim.CaptureFrameInputs:409-431)
    // and hands them over with SetCapsules / SetCloudSpheres.
    //
    // WHY THERE IS NO FOUR-PROBE INSIDE TEST HERE
    // ------------------------------------------
    // A triangle-soup collider decides "this vertex is inside the collider" with FOUR
    // raycast probes multiplied together:
    //   (a) a +Normal ray that must hit a BACK face,
    //   (b) an axis probe, a dual fixed-direction probe at +-(0.5774,0.5774,0.5774)
    //                 that must hit a back face on either leg,
    //   (c) a surrounded veto, the -Normal ray must ALSO hit (a point truly inside
    //                 a volume is surrounded, a back-face false positive is not),
    //   (d) a self-range check, which throws away hits on the SELF mesh further than
    //                 SELF_RANGE (0.10 m) - a ray that escaped through the mask hole and
    //                 crossed the body cavity.
    // Every one of those exists because the collider is an arbitrary triangle soup whose
    // normals may be mirrored, whose faces may be open, and which may be the deforming mesh
    // itself. Our colliders are bone CAPSULES (and the mesh-cloud's auto-fitted capsules):
    // closed, convex, analytic. Against a closed convex primitive (a), (b) and (c) are all
    // exactly the predicate distToSegment(p) < radius - a ray from a point inside a convex
    // body exits through a back face in EVERY direction, and a point outside it satisfies
    // none of them. (d) has no meaning at all, because an analytic primitive is never self
    // geometry. So the analytic test is not a shortcut or an approximation: it is the same
    // predicate for roughly one twentieth of the cost (20 flops against four grid-accelerated
    // raycasts). The ray-based form survives only for the SELF hull - RayCage below - where
    // the collider really is an arbitrary deformed triangle soup and the four probes are
    // genuinely earning their keep.
    public class FieldCollider
    {
        // The ray length is 1000.0 - effectively "infinite"
        // against a character-sized scene. The shared contract sketched 10 m; 1000 m is
        // kept because the escape rays must never run out of length inside a large
        // collider. Behaviour is identical for any hit inside 10 m.
        public const float RayLength = 1000f;

        // bone capsules (proxy-local, captured on the main thread)
        Vector3[] ca, cb; float[] cr; bool[] cv; int nc;
        bool[] climb;          // capsule belongs to a hand / finger / arm
        Vector3[] cmid; float[] creach;   // bounding sphere per capsule: a point outside it
                                          // cannot be inside the capsule, so it is skipped
        public bool lastLimb;  // the deepest capsule of the last InsideAnalytic hit was a limb
        // escape distance of the last FleshConform that SELF_RANGE rejected (0 otherwise). A
        // hand buried deeper than 10 cm is thrown out of the conform, but Lift still needs
        // to know it is there and how deep.
        public float lastDeepPen;
        // mesh-cloud primitives: MeshColliderCloud has already reduced the skinned mesh to
        // bone capsules, so each entry is a capsule too - a sphere is just a==b.
        Vector3[] sa, sb; float[] sr; int ns;

        public int CapsuleCount { get { return nc + ns; } }
        public bool Any { get { return nc + ns > 0; } }

        // ---------------------------------------------------------------- setters
        // Both setters COPY. The caller's capture arrays are reused every frame and, once the
        // field runs off the main thread, would be rewritten underneath us mid-sweep.
        public void SetCapsules(Vector3[] a, Vector3[] b, float[] r, bool[] valid, int count)
        { SetCapsules(a, b, r, valid, null, count); }

        public void SetCapsules(Vector3[] a, Vector3[] b, float[] r, bool[] valid, bool[] limb, int count)
        {
            if (count < 0) count = 0;
            // GROW-ONLY. `nc` is the live count, so a shorter frame just uses less of the
            // buffer — reallocating on every count change put four arrays a frame on the heap
            // the moment a collider was toggled or a mesh cloud resized.
            if (ca == null || ca.Length < count)
            { ca = new Vector3[count]; cb = new Vector3[count]; cr = new float[count]; cv = new bool[count]; climb = new bool[count];
              cmid = new Vector3[count]; creach = new float[count]; }
            nc = 0;
            for (int i = 0; i < count; i++)
            {
                if (valid != null && i < valid.Length && !valid[i]) continue;
                if (r == null || i >= r.Length || r[i] <= 0f) continue;
                ca[nc] = a[i]; cb[nc] = b != null && i < b.Length ? b[i] : a[i]; cr[nc] = r[i]; cv[nc] = true;
                climb[nc] = limb != null && i < limb.Length && limb[i];
                cmid[nc] = (ca[nc] + cb[nc]) * 0.5f;
                creach[nc] = (cb[nc] - ca[nc]).magnitude * 0.5f + cr[nc];
                nc++;
            }
        }

        public void SetCloudSpheres(Vector3[] c, float[] r, int count)
        {
            if (count < 0) count = 0;
            if (sa == null || sa.Length < count)   // grow-only; `ns` is the live count
            { sa = new Vector3[count]; sb = new Vector3[count]; sr = new float[count]; }
            ns = 0;
            for (int i = 0; i < count; i++)
            {
                if (r == null || i >= r.Length || r[i] <= 0f) continue;
                sa[ns] = c[i]; sb[ns] = c[i]; sr[ns] = r[i];
                ns++;
            }
        }

        // Cloud capsules straight from MeshColliderCloud (a,b,r) - same store as the spheres,
        // used when the caller has the real segments rather than centres only.
        public void SetCloudCapsules(Vector3[] a, Vector3[] b, float[] r, int count)
        {
            if (count < 0) count = 0;
            if (sa == null || sa.Length < count)   // grow-only; `ns` is the live count
            { sa = new Vector3[count]; sb = new Vector3[count]; sr = new float[count]; }
            ns = 0;
            for (int i = 0; i < count; i++)
            {
                if (r == null || i >= r.Length || r[i] <= 0f) continue;
                sa[ns] = a[i]; sb[ns] = b != null && i < b.Length ? b[i] : a[i]; sr[ns] = r[i];
                ns++;
            }
        }

        // index across both stores, so every sweep is one loop over CapsuleCount
        void Cap(int k, out Vector3 a, out Vector3 b, out float r)
        {
            if (k < nc) { a = ca[k]; b = cb[k]; r = cr[k]; }
            else { int j = k - nc; a = sa[j]; b = sb[j]; r = sr[j]; }
        }

        // ---------------------------------------------------------------- analytic tests
        // Deepest penetration wins: with two capsules overlapping the same flesh the shallow
        // one would otherwise decide the escape direction and leave the vertex inside the
        // other. cn is the OUTWARD surface normal, cp the surface point under p.
        // NO SIDE EFFECTS: this runs from several threads at once (contact prepare,
        // cleanup and anti-clip). Which kind of capsule was hit comes back through `limb`;
        // only FleshConform, which is single-threaded, stores it in lastLimb.
        public bool InsideAnalytic(Vector3 p, out float depth, out Vector3 cp, out Vector3 cn)
        {
            bool limb;
            return InsideAnalytic(p, out depth, out cp, out cn, out limb);
        }

        public bool InsideAnalytic(Vector3 p, out float depth, out Vector3 cp, out Vector3 cn, out bool limb)
        {
            depth = 0f; cp = p; cn = Vector3.up;
            bool hit = false;
            limb = false;
            int total = nc + ns;
            for (int k = 0; k < total; k++)
            {
                if (k < nc && cmid != null)
                {
                    float rr = creach[k];
                    if ((p - cmid[k]).sqrMagnitude > rr * rr) continue;   // outside its bounding sphere
                }
                Vector3 a, b; float r;
                Cap(k, out a, out b, out r);
                Vector3 s = ClosestOnSegment(a, b, p);
                Vector3 d = p - s;
                float dist = d.magnitude;
                if (dist >= r) continue;
                float dep = r - dist;
                if (hit && dep <= depth) continue;
                depth = dep; hit = true;
                limb = k < nc && climb != null && k < climb.Length && climb[k];
                cn = dist > 1e-7f ? d / dist : Vector3.up;   // dead centre: any direction is legal
                cp = s + cn * r;
            }
            return hit;
        }

        // Nearest point on the union's SURFACE - the analytic stand-in for Blender's
        // Geometry Proximity. Ties are broken on |dist - radius| so a point inside one
        // capsule and outside another still escapes through the capsule it is inside.
        public bool ClosestAnalytic(Vector3 p, out Vector3 cp, out Vector3 cn, out float dist)
        {
            cp = p; cn = Vector3.up; dist = 0f;
            bool hit = false; float best = float.MaxValue;
            int total = nc + ns;
            for (int k = 0; k < total; k++)
            {
                if (hit && k < nc && cmid != null)
                {
                    // |p - surface| >= |p - mid| - reach for every point of this capsule
                    float lb = (p - cmid[k]).magnitude - creach[k];
                    if (lb > best) continue;
                }
                Vector3 a, b; float r;
                Cap(k, out a, out b, out r);
                Vector3 s = ClosestOnSegment(a, b, p);
                Vector3 d = p - s;
                float dd = d.magnitude;
                float surf = dd - r;
                float ad = surf < 0f ? -surf : surf;
                if (hit && ad >= best) continue;
                best = ad; hit = true;
                cn = dd > 1e-7f ? d / dd : Vector3.up;
                cp = s + cn * r;
                dist = ad;
            }
            return hit;
        }

        // Classic contact conform (the pass-2 "hard cleanup" form).
        // The SKIN RAMP is the load-bearing part: eff = offset * min(pen / (2*offset + 1e-5), 1).
        // A grazing contact (pen near zero) gets no skin at all and therefore does not ADHERE
        // to the collider; firm pressure gets the full anti-clip thickness. Dropping the ramp
        // and using a flat offset makes flesh stick to anything it brushes past. The 1e-5 is
        // the guard for offset == 0 and must stay.
        public bool Conform(Vector3 p, Vector3 n, float offset, out Vector3 conform, out float pen)
        {
            conform = Vector3.zero; pen = 0f;
            float depth; Vector3 icp, icn;
            if (!InsideAnalytic(p, out depth, out icp, out icn)) return false;

            Vector3 closest; Vector3 cn2; float cd;
            if (!ClosestAnalytic(p, out closest, out cn2, out cd)) return false;

            Vector3 escRaw = closest - p;
            pen = escRaw.magnitude;
            Vector3 escape = pen > 1e-7f ? escRaw / pen : icn;
            float ramp = Mathf.Min(pen / (offset * 2f + 1e-5f), 1f);
            float eff = offset * ramp;
            Vector3 target = closest + escape * eff;
            conform = target - p;                     // inside == 1 on this branch
            return true;
        }

        // FLESH conform. Lives here rather than in
        // ContactSource because PenetrationSource needs the identical maths against the same
        // primitives and duplicating sixty lines of escape blending is how the two drift apart.
        //   escape 1 (FLATTEN) - ray along the contact push axis, the face that is pressing;
        //   escape 2 (SPILL)   - radial ray perpendicular to that axis, away from the contact
        //                        centroid, the overflow around the object's flank.
        // NOTE, and it is not a bug: against a CAPSULE the spill half is largely inert. A
        // capsule has no edge and no flat face, so every radial ray exits through the same
        // smoothly curved surface and the axial escape almost always wins the blend. Splay is
        // implemented faithfully because it is nearly free, not because it is a live slider.
        // SELF_RANGE: the 0.10 m self-hit cutoff. Off restores the pre-guard behaviour for an A/B.
        public const float SELF_RANGE = 0.10f;
        public static bool selfRangeGuard = true;
        public static int selfRangeRejects;

        public bool FleshConform(Vector3 p, Vector3 nrm, Vector3 push, Vector3 cContact,
                                 float axisQ, float splay, float offset,
                                 out Vector3 conform, out float pen, out Vector3 escapeDir)
        {
            conform = Vector3.zero; pen = 0f; escapeDir = nrm;
            lastDeepPen = 0f;
            float depth; Vector3 icp, icn;
            bool hitLimb;
            if (!InsideAnalytic(p, out depth, out icp, out icn, out hitLimb)) { lastLimb = false; return false; }
            lastLimb = hitLimb;

            // escape 1: flattening along the push axis
            float tA = 0f; Vector3 posA = p, nrmA = nrm;
            bool hitA = push.sqrMagnitude > 1e-12f && Ray(p, push, RayLength, out tA, out posA, out nrmA);
            if (!hitA) { tA = 0f; posA = p; }
            float validA = hitA ? 1f : 0f;

            // escape 2: radial spill, perpendicular to the axis, away from the centroid
            Vector3 rel = p - cContact;
            Vector3 tang = rel - push * Vector3.Dot(rel, push);
            float tl = tang.magnitude;
            Vector3 rdir = tl > 1e-7f ? tang / tl : Vector3.zero;
            float tR = 0f; Vector3 posR = p, nrmR = nrm;
            bool hitR = rdir.sqrMagnitude > 1e-12f && Ray(p, rdir, RayLength, out tR, out posR, out nrmR);
            if (!hitR) { tR = 0f; posR = p; }
            float validR = hitR ? 1f : 0f;

            // ---- ROUND-COLLIDER SPILL ------------------------------------------------
            // The standard sideways escape is a ray fired perpendicular to the push axis, and
            // it was tuned for colliders with FLAT faces and EDGES: flesh near an edge has a
            // short way out sideways. A capsule has neither. Fired from inside an arm, that
            // ray runs along the arm's length and exits far away, the 5 cm spill cap then
            // kills it, and Splay did nothing at any setting.
            // A round collider's real sideways way out is AROUND its curve: the nearest point
            // on its surface (icp). Under the middle of the arm that is straight back out,
            // the same as flattening; near its sides it is sideways round the cylinder,
            // which is exactly "spilling past the edge" for a round thing. So when Splay is
            // up and the curve is the shorter way out, it becomes the sideways escape.
            // Splay 0 skips this entirely: identical to before.
            if (splay > 0f)
            {
                float tS = (icp - p).magnitude;
                if (!hitR || tS < tR)
                {
                    tR = tS; posR = icp; nrmR = icn; validR = 1f; hitR = true;
                }
            }

            // splay-biased smooth-min: the SHORT escape wins, continuously.
            float dA2 = tA * tA;
            float dR2 = tR * tR;
            // Spill cap. Flesh is cohesive: only flesh already NEAR an edge spills
            // over it. fall = 1/(1+(dR/5cm)^2) = 1/(1+400*dR^2). Without it a large collider
            // let mid-breast flesh "melt" 10-15 cm sideways to the far face.
            float fall = 1f / (1f + dR2 * 400f);
            float num = dA2 * splay * fall;
            float den = num + dR2 * (1f - splay) + 1e-8f;
            float wR = num / den;
            // The three ordered fallbacks, in a fixed order. Order matters: the
            // axis-quality one must be able to override the valid-ray one, not the reverse.
            wR *= validR;
            float f1 = (1f - validA) * validR; if (f1 > wR) wR = f1;
            float f2 = (1f - axisQ) * validR; if (f2 > wR) wR = f2;
            float anyHit = validA > validR ? validA : validR;

            Vector3 target = posA * (1f - wR) + posR * wR;

            Vector3 esc = target - p;
            pen = esc.magnitude;
            // ---- SELF_RANGE ---------------------------------------------------------
            // A REAL penetration leaves through the near wall. An escape that has to travel
            // further than 10 cm is the inside test having gone wrong: the node was never
            // really in contact, it is sitting inside a LARGE capsule (a torso, an upper
            // arm) and the exit ray is crossing the whole body cavity to the far side.
            //
            // 68.7 mm of phantom deformation was measured in the decolletage from
            // exactly this, hence the guard. Here it maps one-for-one, because `pen` IS
            // the ray's hit distance - the quantity compared against SELF_RANGE.
            //
            // Measured before this guard: cage max 139 mm on a region 193 mm across, mean
            // 2.2 mm. A handful of nodes flying a distance comparable to the whole breast,
            // while their neighbours barely moved - which renders as shards.
            //
            // Rejecting is the right answer, not clamping: a clamp would still displace the
            // node by 100 mm in a direction chosen by a bogus hit. Not-inside is what the
            // node actually is.
            if (selfRangeGuard && pen > SELF_RANGE)
            {
                selfRangeRejects++;
                lastDeepPen = pen;
                conform = Vector3.zero; pen = 0f; escapeDir = nrm;
                return false;
            }

            Vector3 escN = pen > 1e-7f ? esc / pen : icn;
            escapeDir = escN;
            float ramp = Mathf.Min(pen / (offset * 2f + 1e-5f), 1f);
            Vector3 target2 = target + escN * (offset * ramp);
            conform = (target2 - p) * anyHit;
            return true;
        }

        // ---------------------------------------------------------------- rays
        // Nearest hit across every primitive. The escape rays START INSIDE the collider, so
        // the near root is BEHIND the origin and must be rejected - taking the first root
        // unconditionally teleports the vertex to the entry face.
        public bool Ray(Vector3 o, Vector3 d, float maxLen,
                        out float t, out Vector3 hitPos, out Vector3 hitNrm)
        {
            t = 0f; hitPos = o; hitNrm = Vector3.up;
            float dl = d.magnitude;
            if (dl < 1e-7f) return false;
            Vector3 dir = d / dl;
            if (maxLen <= 0f) maxLen = RayLength;

            float best = maxLen; bool hit = false;
            int total = nc + ns;
            for (int k = 0; k < total; k++)
            {
                Vector3 a, b; float r;
                Cap(k, out a, out b, out r);
                float tt; Vector3 hp, hn;
                if (!RayCapsule(o, dir, a, b, r, best, out tt, out hp, out hn)) continue;
                best = tt; hitPos = hp; hitNrm = hn; hit = true;
            }
            t = hit ? best : 0f;
            return hit;
        }

        // Analytic ray vs capsule: infinite-cylinder body plus the two sphere caps. Returns
        // the nearest hit strictly in FRONT of the origin and closer than maxT.
        static bool RayCapsule(Vector3 o, Vector3 dir, Vector3 A, Vector3 B, float r, float maxT,
                               out float t, out Vector3 hitPos, out Vector3 hitNrm)
        {
            t = 0f; hitPos = o; hitNrm = Vector3.up;
            bool hit = false; float best = maxT;

            Vector3 ba = B - A;
            float baba = Vector3.Dot(ba, ba);
            Vector3 oa = o - A;

            if (baba > 1e-12f)
            {
                float bard = Vector3.Dot(ba, dir);
                float baoa = Vector3.Dot(ba, oa);
                float rdoa = Vector3.Dot(dir, oa);
                float oaoa = Vector3.Dot(oa, oa);
                float aq = baba - bard * bard;                       // 0 when the ray is axial
                float bq = baba * rdoa - baoa * bard;
                float cq = baba * oaoa - baoa * baoa - r * r * baba;
                float h = bq * bq - aq * cq;
                if (aq > 1e-12f && h >= 0f)
                {
                    float sh = Mathf.Sqrt(h);
                    for (int s = 0; s < 2; s++)
                    {
                        float tt = (s == 0 ? (-bq - sh) : (-bq + sh)) / aq;
                        if (tt <= 1e-6f || tt >= best) continue;
                        float y = baoa + tt * bard;
                        if (y <= 0f || y >= baba) continue;           // past an end: the cap handles it
                        Vector3 hp = o + dir * tt;
                        Vector3 axisPt = A + ba * (y / baba);
                        best = tt; hitPos = hp; hitNrm = (hp - axisPt) / r; hit = true;
                        break;                                        // roots ascend: first valid is nearest
                    }
                }
            }

            // caps (also the whole primitive when A == B)
            for (int e = 0; e < 2; e++)
            {
                Vector3 c = e == 0 ? A : B;
                if (e == 1 && baba <= 1e-12f) break;
                Vector3 oc = o - c;
                float bb = Vector3.Dot(dir, oc);
                float cc = Vector3.Dot(oc, oc) - r * r;
                float hh = bb * bb - cc;
                if (hh < 0f) continue;
                float sh2 = Mathf.Sqrt(hh);
                for (int s = 0; s < 2; s++)
                {
                    float tt = s == 0 ? (-bb - sh2) : (-bb + sh2);
                    if (tt <= 1e-6f || tt >= best) continue;
                    Vector3 hp = o + dir * tt;
                    if (baba > 1e-12f)
                    {
                        // only the hemisphere BEYOND the segment end is real surface; the rest
                        // of the sphere is buried in the cylinder
                        float y = Vector3.Dot(ba, hp - A);
                        if (e == 0 ? y > 0f : y < baba) continue;
                    }
                    best = tt; hitPos = hp; hitNrm = (hp - c) / r; hit = true;
                    break;
                }
            }

            t = best;
            return hit;
        }

        // ---------------------------------------------------------------- self hull
        // Ray against the DEFORMED cage triangles, for self-contact. This is the one place the
        // arbitrary-triangle-soup case survives, and it needs an accelerator: brute force over
        // ~9000 cage triangles per ray is not a budget, it is a slideshow.
        //
        // RemeshCage.TriGrid is public but exposes only Nearest/NearestGated - its cell
        // dictionary, P and T are private, so it cannot serve a ray query from outside the
        // class and there is no way to add one without editing RemeshCage.cs, which is not
        // ours. So this is our own grid with the same cell hashing and a 3D-DDA walk.
        // STALENESS, and why the throttle is not enough on its own: the arrays are ALIASED,
        // so RayTri always reads the caller's CURRENT vertex positions while the cell buckets
        // date from whenever the grid was last built. Those two disagreeing is not "one or two
        // frames stale", it is a grid that lists a triangle in a cell the triangle has left -
        // the DDA then walks past it and the ray misses. So the caller that is about to shoot
        // rays forces a rebuild (SetCageForSelf's `force`), and SelfRebuild only throttles
        // repeat calls inside the same solve.
        const float SelfRebuild = 0.2f;

        Vector3[] selfP; int[] selfT;
        readonly Dictionary<long, List<int>> selfCells = new Dictionary<long, List<int>>();
        float selfCell = 0.02f;
        int[] selfStamp; int selfStampId;
        float selfBuiltAt = -999f;
        int selfBuiltTris = -1;

        public bool HasSelfHull { get { return selfT != null && selfCells.Count > 0; } }

        // Pass the deformed cage positions, its triangle list, and the frame's clock. The
        // arrays are aliased, not copied - the grid indexes into whatever the caller keeps
        // writing, which is exactly what makes the staleness cheap.
        // `now` is a PARAMETER and not Time.realtimeSinceStartup precisely because FieldPost
        // calls this from the solve half, and the solve half is the half that is meant to move
        // to a worker thread one day. Reading a UnityEngine static in here would have made this
        // the one line that blocked it, and it would have thrown rather than degraded.
        public void SetCageForSelf(Vector3[] deformedPos, int[] tris, float now)
        {
            SetCageForSelf(deformedPos, tris, now, false);
        }

        // force: rebuild the buckets from THESE positions now, whatever the throttle says.
        // FieldPost passes true on the first iteration of its self-contact pass, because that
        // is the moment the positions it is about to raycast against become final.
        public void SetCageForSelf(Vector3[] deformedPos, int[] tris, float now, bool force)
        {
            selfP = deformedPos; selfT = tris;
            if (deformedPos == null || tris == null || tris.Length < 3) { selfCells.Clear(); selfBuiltTris = -1; return; }
            if (!force && selfBuiltTris == tris.Length && now - selfBuiltAt < SelfRebuild) return;
            selfBuiltAt = now; selfBuiltTris = tris.Length;
            RebuildSelfGrid();
        }

        void RebuildSelfGrid()
        {
            selfCells.Clear();
            int ntri = selfT.Length / 3;
            if (selfStamp == null || selfStamp.Length != ntri) { selfStamp = new int[ntri]; selfStampId = 0; }

            // cell = ~2 mean triangle edges, the same rule RemeshCage uses for its own grids
            float acc = 0f; int cnt = 0;
            int stride = Mathf.Max(3, (selfT.Length / 3 / 256) * 3);
            for (int i = 0; i + 2 < selfT.Length; i += stride)
            { acc += (selfP[selfT[i + 1]] - selfP[selfT[i]]).magnitude; cnt++; }
            selfCell = cnt > 0 ? Mathf.Clamp(acc / cnt * 2f, 0.004f, 0.08f) : 0.02f;

            for (int i = 0; i + 2 < selfT.Length; i += 3)
            {
                Vector3 a = selfP[selfT[i]], b = selfP[selfT[i + 1]], c = selfP[selfT[i + 2]];
                Vector3 mn = Vector3.Min(a, Vector3.Min(b, c)), mx = Vector3.Max(a, Vector3.Max(b, c));
                int x0 = Mathf.FloorToInt(mn.x / selfCell), x1 = Mathf.FloorToInt(mx.x / selfCell);
                int y0 = Mathf.FloorToInt(mn.y / selfCell), y1 = Mathf.FloorToInt(mx.y / selfCell);
                int z0 = Mathf.FloorToInt(mn.z / selfCell), z1 = Mathf.FloorToInt(mx.z / selfCell);
                if ((long)x1 - x0 > 96 || (long)y1 - y0 > 96 || (long)z1 - z0 > 96) continue;   // degenerate/NaN
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        for (int z = z0; z <= z1; z++)
                        {
                            long k = CKey(x, y, z);
                            List<int> l;
                            if (!selfCells.TryGetValue(k, out l)) { l = new List<int>(4); selfCells[k] = l; }
                            l.Add(i);
                        }
            }
        }

        static long CKey(int x, int y, int z)
        {
            // the (uint) is only there to keep the compiler quiet about sign extension;
            // the operand is already masked to 21 bits so the value is unchanged
            return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(uint)(z & 0x1FFFFF);
        }

        // 3D DDA (Amanatides & Woo) along the ray, testing each cell's triangles once. Hits are
        // double-sided: Blender's Raycast hits back faces too, and the self hull is exactly the
        // case where the back face is the one that matters.
        public bool RayCage(Vector3 o, Vector3 d, float maxLen,
                            out float t, out Vector3 hitPos, out Vector3 hitNrm)
        {
            t = 0f; hitPos = o; hitNrm = Vector3.up;
            if (selfT == null || selfCells.Count == 0) return false;
            float dl = d.magnitude;
            if (dl < 1e-7f) return false;
            Vector3 dir = d / dl;
            if (maxLen <= 0f) maxLen = RayLength;

            selfStampId++;
            if (selfStampId == int.MaxValue) { for (int i = 0; i < selfStamp.Length; i++) selfStamp[i] = 0; selfStampId = 1; }

            int cx = Mathf.FloorToInt(o.x / selfCell), cy = Mathf.FloorToInt(o.y / selfCell), cz = Mathf.FloorToInt(o.z / selfCell);
            int sx = dir.x > 0f ? 1 : (dir.x < 0f ? -1 : 0);
            int sy = dir.y > 0f ? 1 : (dir.y < 0f ? -1 : 0);
            int sz = dir.z > 0f ? 1 : (dir.z < 0f ? -1 : 0);
            float tmx = AxisTMax(o.x, dir.x, cx, sx), tdx = AxisTDelta(dir.x);
            float tmy = AxisTMax(o.y, dir.y, cy, sy), tdy = AxisTDelta(dir.y);
            float tmz = AxisTMax(o.z, dir.z, cz, sz), tdz = AxisTDelta(dir.z);

            float best = maxLen; bool hit = false;
            for (int step = 0; step < 512; step++)
            {
                List<int> l;
                if (selfCells.TryGetValue(CKey(cx, cy, cz), out l))
                {
                    for (int j = 0; j < l.Count; j++)
                    {
                        int tri = l[j];
                        if (selfStamp[tri / 3] == selfStampId) continue;
                        selfStamp[tri / 3] = selfStampId;
                        float tt; Vector3 hn;
                        if (!RayTri(o, dir, selfP[selfT[tri]], selfP[selfT[tri + 1]], selfP[selfT[tri + 2]], out tt, out hn)) continue;
                        if (tt <= 1e-6f || tt >= best) continue;
                        best = tt; hitNrm = hn; hit = true;
                    }
                }
                float tNext = tmx < tmy ? (tmx < tmz ? tmx : tmz) : (tmy < tmz ? tmy : tmz);
                if (hit && best <= tNext) break;          // provably nearest: nothing further can win
                if (tNext > maxLen) break;
                if (tmx <= tmy && tmx <= tmz) { cx += sx; tmx += tdx; }
                else if (tmy <= tmz) { cy += sy; tmy += tdy; }
                else { cz += sz; tmz += tdz; }
                if (sx == 0 && sy == 0 && sz == 0) break;
            }
            if (!hit) return false;
            t = best; hitPos = o + dir * best;
            return true;
        }

        float AxisTMax(float p, float dv, int c, int s)
        {
            if (s == 0) return float.MaxValue;
            float border = (s > 0 ? (c + 1) : c) * selfCell;
            return (border - p) / dv;
        }

        float AxisTDelta(float dv)
        {
            float a = dv < 0f ? -dv : dv;
            return a < 1e-9f ? float.MaxValue : selfCell / a;
        }

        // Moeller-Trumbore, double sided. hitNrm is the raw geometric normal (NOT flipped to
        // face the ray) so a caller can still read back-face-ness from dot(dir, hitNrm) > 0,
        // which is how ray-based inside probes decide.
        static bool RayTri(Vector3 o, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2,
                           out float t, out Vector3 nrm)
        {
            t = 0f; nrm = Vector3.up;
            Vector3 e1 = v1 - v0, e2 = v2 - v0;
            Vector3 pv = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, pv);
            if (det > -1e-12f && det < 1e-12f) return false;
            float inv = 1f / det;
            Vector3 tv = o - v0;
            float u = Vector3.Dot(tv, pv) * inv;
            if (u < 0f || u > 1f) return false;
            Vector3 qv = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(dir, qv) * inv;
            if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(e2, qv) * inv;
            if (t <= 0f) return false;
            nrm = Vector3.Cross(e1, e2).normalized;
            return true;
        }

        public static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a;
            float t = Vector3.Dot(p - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude);
            return a + ab * Mathf.Clamp01(t);
        }
    }
}
