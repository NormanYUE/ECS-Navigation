using Unity.Mathematics;

namespace Ember.Navigation
{
    /// <summary>
    /// 对角位移的「切角」分解（A* / 流场共用，纯几何，不含可走判据）。
    ///
    /// 一次对角位移在格空间里从一个角穿到另一个角，途中贴着若干<b>中间格</b> ——
    /// 正是位移分量的非空真子集：2D 两个、3D 六个。只看终点的话，搜索会给出
    /// 从两堵墙的夹角斜切过去的路径：两端都可走，中间擦过的那格不可走，
    /// 半径大于零的代理根本过不去 —— 搜索说通、走起来卡死。
    ///
    /// 这条不变式还有第二个消费者：<see cref="NavPathSmoother.HasLineOfSight"/> 的
    /// 超覆盖遍历。搜索保证「相邻航点必然通视」之后，平滑器退回最低等级那趟兜底才真的兜得住；
    /// 否则相邻航点都可能不通视，整条请求被判 Failed。
    ///
    /// 可走判据不放在这里：各调用方手里是 Context / 裸指针，签名各不相同，
    /// 硬凑一个签名反而要多一份「谁的 IsWalkable」的分歧。这里只出分解结果。
    /// </summary>
    internal static class NavCorner
    {
        /// <summary>该位移是否跨了不止一个轴。</summary>
        public static bool IsDiagonal(int dx, int dy, int dz)
        {
            return math.abs(dx) + math.abs(dy) + math.abs(dz) > 1;
        }

        /// <summary>第 <paramref name="mask"/> 种分量组合的偏移（位 1=x、2=y、4=z）。</summary>
        public static int3 Offset(int mask, int dx, int dy, int dz)
        {
            return new int3(
                (mask & 1) != 0 ? dx : 0,
                (mask & 2) != 0 ? dy : 0,
                (mask & 4) != 0 ? dz : 0);
        }

        /// <summary>
        /// 该组合是否为「中间格」——全零与整个位移都不算：
        /// 全零是原格，整个位移是终点本身（调用方已另行校验）。
        /// </summary>
        public static bool IsPartial(int3 offset, int dx, int dy, int dz)
        {
            if (offset.x == 0 && offset.y == 0 && offset.z == 0) return false;
            if (offset.x == dx && offset.y == dy && offset.z == dz) return false;
            return true;
        }
    }
}
