using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// Least-squares rigid registration (tracker-ik map t05): solves the
    /// proper rotation R and translation t minimizing Σ|R·p_i + t − q_i|².
    /// Horn's quaternion method — the largest-eigenvalue eigenvector of the
    /// 4×4 cross-covariance N-matrix, via Jacobi rotations (no SVD
    /// dependency). Degenerate point spreads (collinear / near-collinear)
    /// are refused: the rotation about the spread's thin axis would fit
    /// noise. Reflection-flavoured data (det&lt;0 mappings) cannot be fit by
    /// a proper rotation at all — they surface as a large residual that the
    /// caller's gate rejects.
    /// </summary>
    public static class KabschSolver
    {
        // Any triangle flatter than this (m²) leaves the rotation about its
        // normal underdetermined (heuristic default, device rounds may tune).
        private const float MinTriangleArea = 0.0009f;

        public static bool Solve(
            Vector3[] from, Vector3[] to,
            out Quaternion rotation, out Vector3 translation, out float positionRms)
        {
            rotation = Quaternion.identity;
            translation = Vector3.zero;
            positionRms = 0f;

            if (from == null || to == null || from.Length != to.Length || from.Length < 3)
                return false;
            if (IsDegenerate(from) || IsDegenerate(to))
                return false;

            int n = from.Length;
            var fromCentroid = Vector3.zero;
            var toCentroid = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                fromCentroid += from[i];
                toCentroid += to[i];
            }
            fromCentroid /= n;
            toCentroid /= n;

            // Cross-covariance of the centered sets (Horn 1987).
            float sxx = 0f, sxy = 0f, sxz = 0f;
            float syx = 0f, syy = 0f, syz = 0f;
            float szx = 0f, szy = 0f, szz = 0f;
            for (int i = 0; i < n; i++)
            {
                var f = from[i] - fromCentroid;
                var t = to[i] - toCentroid;
                sxx += f.x * t.x; sxy += f.x * t.y; sxz += f.x * t.z;
                syx += f.y * t.x; syy += f.y * t.y; syz += f.y * t.z;
                szx += f.z * t.x; szy += f.z * t.y; szz += f.z * t.z;
            }

            // Horn's symmetric 4×4 N-matrix; its top eigenvector is the
            // optimal from->to rotation quaternion (w, x, y, z).
            var n4 = new float[4, 4];
            n4[0, 0] = sxx + syy + szz;
            n4[0, 1] = syz - szy;      n4[1, 0] = n4[0, 1];
            n4[0, 2] = szx - sxz;      n4[2, 0] = n4[0, 2];
            n4[0, 3] = sxy - syx;      n4[3, 0] = n4[0, 3];
            n4[1, 1] = sxx - syy - szz;
            n4[1, 2] = sxy + syx;      n4[2, 1] = n4[1, 2];
            n4[1, 3] = sxz + szx;      n4[3, 1] = n4[1, 3];
            n4[2, 2] = -sxx + syy - szz;
            n4[2, 3] = syz + szy;      n4[3, 2] = n4[2, 3];
            n4[3, 3] = -sxx - syy + szz;

            var ev = LargestEigenvector4(n4);
            rotation = new Quaternion(ev.y, ev.z, ev.w, ev.x);
            // Canonical sign (q and -q are the same rotation).
            if (rotation.w < 0f)
                rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
            translation = toCentroid - rotation * fromCentroid;

            float sumSq = 0f;
            for (int i = 0; i < n; i++)
            {
                var d = rotation * from[i] + translation - to[i];
                sumSq += d.sqrMagnitude;
            }
            positionRms = Mathf.Sqrt(sumSq / n);
            return true;
        }

        /// <summary>Collinear / near-collinear spread check (3 points: the
        /// triangle area; more: the best triangle anchored at points[0]).</summary>
        public static bool IsDegenerate(Vector3[] points)
        {
            if (points == null || points.Length < 3)
                return true;

            if (points.Length == 3)
                return TriangleArea(points[0], points[1], points[2]) < MinTriangleArea;

            float best = 0f;
            for (int i = 1; i < points.Length; i++)
            {
                for (int j = i + 1; j < points.Length; j++)
                    best = Mathf.Max(best, TriangleArea(points[0], points[i], points[j]));
            }
            return best < MinTriangleArea;
        }

        private static float TriangleArea(Vector3 a, Vector3 b, Vector3 c) =>
            0.5f * Vector3.Cross(b - a, c - a).magnitude;

        /// <summary>Jacobi eigenvalue iteration for a symmetric 4×4; returns
        /// the unit eigenvector of the LARGEST eigenvalue.</summary>
        private static Vector4 LargestEigenvector4(float[,] a)
        {
            var m = (float[,])a.Clone();
            var v = new float[4, 4];
            for (int i = 0; i < 4; i++)
                v[i, i] = 1f;

            for (int sweep = 0; sweep < 64; sweep++)
            {
                float off = 0f;
                for (int i = 0; i < 4; i++)
                    for (int j = i + 1; j < 4; j++)
                        off += m[i, j] * m[i, j];
                if (off < 1e-20f)
                    break;

                for (int p = 0; p < 4; p++)
                {
                    for (int q = p + 1; q < 4; q++)
                    {
                        float apq = m[p, q];
                        if (Mathf.Abs(apq) < 1e-12f)
                            continue;
                        float theta = (m[q, q] - m[p, p]) / (2f * apq);
                        float sign = theta >= 0f ? 1f : -1f;
                        float t = sign / (Mathf.Abs(theta) + Mathf.Sqrt(theta * theta + 1f));
                        float c = 1f / Mathf.Sqrt(t * t + 1f);
                        float s = t * c;

                        for (int k = 0; k < 4; k++)
                        {
                            float mkp = m[k, p], mkq = m[k, q];
                            m[k, p] = c * mkp - s * mkq;
                            m[k, q] = s * mkp + c * mkq;
                        }
                        for (int k = 0; k < 4; k++)
                        {
                            float mpk = m[p, k], mqk = m[q, k];
                            m[p, k] = c * mpk - s * mqk;
                            m[q, k] = s * mpk + c * mqk;
                        }
                        for (int k = 0; k < 4; k++)
                        {
                            float vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - s * vkq;
                            v[k, q] = s * vkp + c * vkq;
                        }
                    }
                }
            }

            int best = 0;
            for (int i = 1; i < 4; i++)
                if (m[i, i] > m[best, best])
                    best = i;
            return new Vector4(v[0, best], v[1, best], v[2, best], v[3, best]).normalized;
        }
    }
}
