using UnityEngine;

namespace SoftBodySuite.Field
{
    // 4D value noise, evaluated LIVE — there is deliberately no precomputed
    // slice table. At the default Wobble Speed of 2.0-2.5 the field decorrelates
    // in roughly 0.2 s, so eight slices spread over 4 s would crossfade between
    // completely unrelated fields and read as a slow morph instead of a jiggle.
    // Live evaluation is 0.1-0.2 ms at 4500 nodes.
    //
    // Stands in for Blender's ShaderNodeTexNoise (4D, Detail 2.0, Roughness 0.55).
    // That node is Perlin, not value noise, and
    // its hash, gradient set, per-channel offsets and fBm normalisation are all
    // unrecorded, so an exact port is not possible. What IS reproducible is the
    // STATISTIC, and that is what the amplitude sliders were tuned against.
    //
    // CALIBRATION PROCEDURE (re-run this against a Blender bake if the numbers
    // are ever questioned):
    //   1. In Blender, wire a 4D Noise Texture (Scale 2.2, Detail 2, Roughness
    //      0.55) into a Store Named Attribute, subtract 0.5, bake one frame.
    //   2. Take the RMS of one channel over every vertex of the painted region.
    //   3. Set RmsScale so this generator's RMS over a comparable sample equals
    //      that number.
    // The shipped default targets 0.28868 = the RMS of a uniform [-0.5, 0.5],
    // which is what Blender's centred Color output measures to within a few
    // percent. It is measured, not normalised: a textbook fBm normalised to full
    // range swings 2-4x wider, and Glute Jiggle at Amplitude 0.06 would read as
    // violence rather than as fat.
    //
    // Allocation-free, thread-safe, static. Safe to call from worker threads.
    public static class ValueNoise
    {
        // measured: raw 2-octave/0.55 RMS is 0.19227, so 0.288675/0.19227 = 1.5014
        public static float RmsScale = 1.5014f;

        // Blender reads the noise node's Color output, which is three channels of
        // the same field sampled at different offsets. The exact offsets are NOT
        // RECORDED anywhere, so these are chosen: three
        // large, mutually irrational-ish lattice shifts that decorrelate the
        // channels without repeating for any plausible input range.
        const float ChanOffX = 0f;
        const float ChanOffY = 137.13f;
        const float ChanOffZ = 311.71f;

        public static float Value4D(float x, float y, float z, float w,
                                    float scale, int octaves, float roughness)
        {
            if (octaves < 1) octaves = 1;
            if (octaves > 4) octaves = 4;
            float px = x * scale, py = y * scale, pz = z * scale, pw = w * scale;
            float amp = 1f, freq = 1f, sum = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += Octave(px * freq, py * freq, pz * freq, pw * freq, o * 1013) * amp;
                amp *= roughness;
                freq *= 2f;
            }
            return sum * RmsScale;
        }

        // three decorrelated channels of the same field — the Color output
        public static Vector3 Value4DVec(Vector3 p, float w, float scale)
        {
            float x = p.x * scale, y = p.y * scale, z = p.z * scale, ww = w * scale;
            Vector3 r;
            r.x = Fbm2(x + ChanOffX, y + ChanOffX, z + ChanOffX, ww);
            r.y = Fbm2(x + ChanOffY, y + ChanOffY, z + ChanOffY, ww);
            r.z = Fbm2(x + ChanOffZ, y + ChanOffZ, z + ChanOffZ, ww);
            return r;
        }

        // the fixed 2-octave / roughness 0.55 form the noise uses
        static float Fbm2(float x, float y, float z, float w)
        {
            float a = Octave(x, y, z, w, 0);
            float b = Octave(x * 2f, y * 2f, z * 2f, w * 2f, 1013);
            return (a + b * 0.55f) * RmsScale;
        }

        // one octave: quintic-smoothstepped 4D lattice interpolation
        static float Octave(float x, float y, float z, float w, int seed)
        {
            float fx = Mathf.Floor(x), fy = Mathf.Floor(y);
            float fz = Mathf.Floor(z), fw = Mathf.Floor(w);
            int xi = (int)fx, yi = (int)fy, zi = (int)fz, wi = (int)fw;
            float tx = Quintic(x - fx), ty = Quintic(y - fy);
            float tz = Quintic(z - fz), tw = Quintic(w - fw);

            float c000 = Lerp(Hash(xi, yi, zi, wi, seed), Hash(xi + 1, yi, zi, wi, seed), tx);
            float c100 = Lerp(Hash(xi, yi + 1, zi, wi, seed), Hash(xi + 1, yi + 1, zi, wi, seed), tx);
            float c010 = Lerp(Hash(xi, yi, zi + 1, wi, seed), Hash(xi + 1, yi, zi + 1, wi, seed), tx);
            float c110 = Lerp(Hash(xi, yi + 1, zi + 1, wi, seed), Hash(xi + 1, yi + 1, zi + 1, wi, seed), tx);
            float d000 = Lerp(Hash(xi, yi, zi, wi + 1, seed), Hash(xi + 1, yi, zi, wi + 1, seed), tx);
            float d100 = Lerp(Hash(xi, yi + 1, zi, wi + 1, seed), Hash(xi + 1, yi + 1, zi, wi + 1, seed), tx);
            float d010 = Lerp(Hash(xi, yi, zi + 1, wi + 1, seed), Hash(xi + 1, yi, zi + 1, wi + 1, seed), tx);
            float d110 = Lerp(Hash(xi, yi + 1, zi + 1, wi + 1, seed), Hash(xi + 1, yi + 1, zi + 1, wi + 1, seed), tx);

            float a0 = Lerp(Lerp(c000, c100, ty), Lerp(c010, c110, ty), tz);
            float a1 = Lerp(Lerp(d000, d100, ty), Lerp(d010, d110, ty), tz);
            return Lerp(a0, a1, tw);
        }

        static float Quintic(float t) { return t * t * t * (t * (t * 6f - 15f) + 10f); }
        static float Lerp(float a, float b, float t) { return a + (b - a) * t; }

        // integer hash -> uniform [-0.5, 0.5]
        static float Hash(int x, int y, int z, int w, int seed)
        {
            unchecked
            {
                uint n = (uint)(x * 374761393 + y * 668265263 + z * 2147483647
                                + w * 1274126177 + seed * 362437);
                n ^= n >> 13;
                n *= 1274126177u;
                n ^= n >> 16;
                return (n & 0xFFFFFFu) * (1f / 16777215f) - 0.5f;
            }
        }
    }
}
