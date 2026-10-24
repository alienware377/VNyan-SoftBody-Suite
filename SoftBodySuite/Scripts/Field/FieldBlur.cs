using UnityEngine;

namespace SoftBodySuite.Field
{
    // Diffusion over the cage graph, reproducing Blender's GeometryNodeBlurAttribute.
    // Each iteration mixes a node's value toward the mean of its topological neighbours by
    // `weight`; the vector footprint uses 0.85 and
    // the halo 0.9. Radius grows with the SQUARE ROOT of the iteration count,
    // which is why FieldPost.ScaledIters multiplies by dens^2 and not dens.
    //
    // THE BOUNDARY ARGUMENT - READ THIS BEFORE TRUSTING THE NUMBERS
    // ------------------------------------------------------------
    // In Blender the blur runs over the WHOLE mesh's edge graph. The attribute is zero
    // outside the painted mask, but those exterior vertices still take part, so a node at the
    // mask rim is pulled toward zero by its zero-valued neighbours: an ABSORBING boundary.
    // Our cage physically ENDS at the paint rim - it has no exterior neighbours at all - so a
    // plain graph blur on it REFLECTS instead, which is the opposite error and leaves the rim
    // hotter than a whole-mesh blur would.
    //
    // The reconstruction is a normalised convolution: diffuse x*w and w separately and divide
    // at the end, with w = the cage node's PAINT WEIGHT (cage.simWeight). That weight tapers
    // to zero at the rim by construction, because UnionWeights built the cage from it, so rim
    // nodes are down-weighted in the same proportion Blender's zero-valued exterior
    // down-weights them.
    //
    // BE CLEAR ABOUT THE STATUS OF THAT: it is ARGUED, NOT MEASURED. Nobody has yet baked the
    // same contact in Blender and compared rim profiles. Note also that Blender's own Weight
    // socket is a per-ELEMENT field used both as the neighbour weighting AND the self-mix
    // factor, and the effect feeds it a constant - so our split (constant mix factor, mask
    // carried through the num/den channels) is a re-derivation, not a transcription.
    // BlurUnnormalised() is the A/B: same call, no weighting, one flag away from a bake
    // comparison. If the bake says the plain blur is closer, switch and delete this paragraph.
    public class FieldBlur
    {
        CageGraph g;

        // scratch, sized on first use and reused forever - zero allocation per frame
        float[] num, num2, den, den2;
        Vector3[] vnum, vnum2;

        public void SetGraph(CageGraph graph)
        {
            g = graph;
            if (g == null) return;
            if (num == null || num.Length != g.n)
            {
                num = new float[g.n]; num2 = new float[g.n];
                den = new float[g.n]; den2 = new float[g.n];
                vnum = new Vector3[g.n]; vnum2 = new Vector3[g.n];
            }
        }

        public CageGraph Graph { get { return g; } }

        // ---------------------------------------------------------------- scalar
        public void Blur(float[] x, float[] w, int iters, float weight, float[] outp)
        {
            if (g == null || outp == null || x == null) return;
            int n = g.n;
            if (num == null || num.Length != n) SetGraph(g);
            if (iters <= 0)
            {
                for (int i = 0; i < n; i++) outp[i] = x[i];
                return;
            }
            for (int i = 0; i < n; i++)
            {
                float wi = w != null ? w[i] : 1f;
                num[i] = x[i] * wi; den[i] = wi;
            }
            // Each round reads only the previous round (num -> num2), so the nodes are
            // independent within a round and the round can run on every core.
            int[] ns = g.nbrStart, ni = g.nbrIdx;
            for (int it = 0; it < iters; it++)
            {
                float[] A = num, A2 = num2, D = den, D2 = den2;
                ParallelLoop.For(n, delegate (int lo, int hi)
                {
                    for (int i = lo; i < hi; i++)
                    {
                        int s = ns[i], e = ns[i + 1];
                        int k = e - s;
                        if (k <= 0) { A2[i] = A[i]; D2[i] = D[i]; continue; }
                        float an = 0f, ad = 0f;
                        for (int j = s; j < e; j++) { int q = ni[j]; an += A[q]; ad += D[q]; }
                        float inv = 1f / k;
                        A2[i] = A[i] + (an * inv - A[i]) * weight;
                        D2[i] = D[i] + (ad * inv - D[i]) * weight;
                    }
                });
                float[] t = num; num = num2; num2 = t;
                t = den; den = den2; den2 = t;
            }
            for (int i = 0; i < n; i++) outp[i] = num[i] / Mathf.Max(den[i], 1e-6f);
        }

        // ---------------------------------------------------------------- vector
        public void Blur(Vector3[] x, float[] w, int iters, float weight, Vector3[] outp)
        {
            if (g == null || outp == null || x == null) return;
            int n = g.n;
            if (num == null || num.Length != n) SetGraph(g);
            if (iters <= 0)
            {
                for (int i = 0; i < n; i++) outp[i] = x[i];
                return;
            }
            for (int i = 0; i < n; i++)
            {
                float wi = w != null ? w[i] : 1f;
                vnum[i] = x[i] * wi; den[i] = wi;
            }
            int[] ns = g.nbrStart, ni = g.nbrIdx;
            for (int it = 0; it < iters; it++)
            {
                Vector3[] V = vnum, V2 = vnum2; float[] D = den, D2 = den2;
                ParallelLoop.For(n, delegate (int lo, int hi)
                {
                    for (int i = lo; i < hi; i++)
                    {
                        int s = ns[i], e = ns[i + 1];
                        int k = e - s;
                        if (k <= 0) { V2[i] = V[i]; D2[i] = D[i]; continue; }
                        float ax = 0f, ay = 0f, az = 0f, ad = 0f;
                        for (int j = s; j < e; j++) { int q = ni[j]; Vector3 v = V[q]; ax += v.x; ay += v.y; az += v.z; ad += D[q]; }
                        float inv = 1f / k;
                        Vector3 c = V[i];
                        V2[i] = new Vector3(c.x + (ax * inv - c.x) * weight,
                                            c.y + (ay * inv - c.y) * weight,
                                            c.z + (az * inv - c.z) * weight);
                        D2[i] = D[i] + (ad * inv - D[i]) * weight;
                    }
                });
                Vector3[] tv = vnum; vnum = vnum2; vnum2 = tv;
                float[] t = den; den = den2; den2 = t;
            }
            for (int i = 0; i < n; i++) outp[i] = vnum[i] / Mathf.Max(den[i], 1e-6f);
        }

        // ---------------------------------------------------------------- A/B variants
        // Plain graph diffusion, no mask weighting: what a literal whole-mesh blur does
        // once the cage has replaced the render mesh. Kept so the boundary argument above can
        // be settled by measurement instead of prose.
        public void BlurUnnormalised(float[] x, int iters, float weight, float[] outp)
        {
            if (g == null || outp == null || x == null) return;
            int n = g.n;
            if (num == null || num.Length != n) SetGraph(g);
            for (int i = 0; i < n; i++) num[i] = x[i];
            for (int it = 0; it < iters; it++)
            {
                for (int i = 0; i < n; i++)
                {
                    int s = g.nbrStart[i], e = g.nbrStart[i + 1];
                    int k = e - s;
                    if (k <= 0) { num2[i] = num[i]; continue; }
                    float an = 0f;
                    for (int j = s; j < e; j++) an += num[g.nbrIdx[j]];
                    num2[i] = num[i] + (an / k - num[i]) * weight;
                }
                float[] t = num; num = num2; num2 = t;
            }
            for (int i = 0; i < n; i++) outp[i] = num[i];
        }

        public void BlurUnnormalised(Vector3[] x, int iters, float weight, Vector3[] outp)
        {
            if (g == null || outp == null || x == null) return;
            int n = g.n;
            if (num == null || num.Length != n) SetGraph(g);
            for (int i = 0; i < n; i++) vnum[i] = x[i];
            for (int it = 0; it < iters; it++)
            {
                for (int i = 0; i < n; i++)
                {
                    int s = g.nbrStart[i], e = g.nbrStart[i + 1];
                    int k = e - s;
                    if (k <= 0) { vnum2[i] = vnum[i]; continue; }
                    Vector3 an = Vector3.zero;
                    for (int j = s; j < e; j++) an += vnum[g.nbrIdx[j]];
                    vnum2[i] = vnum[i] + (an / k - vnum[i]) * weight;
                }
                Vector3[] tv = vnum; vnum = vnum2; vnum2 = tv;
            }
            for (int i = 0; i < n; i++) outp[i] = vnum[i];
        }
    }
}
