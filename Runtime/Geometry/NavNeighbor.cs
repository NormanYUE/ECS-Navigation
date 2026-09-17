using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 邻格有效性共享实现（流场 / A* / 平滑同一语义）：
    /// 模板（4/8 限制在平面内，6/26 允许立体）+ 界内 + 非自身。
    /// </summary>
    internal static class NavNeighbor
    {
        public static bool IsValid(int3 dims, int connectivity, int3 voxel,
            int dx, int dy, int dz, out int3 neighbor)
        {
            neighbor = default;
            if (dx == 0 && dy == 0 && dz == 0) return false;

            int manhattan = math.abs(dx) + math.abs(dy) + math.abs(dz);
            bool diagonal = connectivity == 8 || connectivity == 26;
            if (manhattan > 1 && !diagonal) return false;

            if ((connectivity == 4 || connectivity == 8) && dims.z == 1 && dz != 0) return false;
            if ((connectivity == 4 || connectivity == 8) && dims.y == 1 && dy != 0) return false;

            int3 n = voxel + new int3(dx, dy, dz);
            if (n.x < 0 || n.y < 0 || n.z < 0 || n.x >= dims.x || n.y >= dims.y || n.z >= dims.z)
                return false;

            neighbor = n;
            return true;
        }
    }
}
