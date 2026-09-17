using Ember.Collision;

namespace Ember.Navigation
{
    /// <summary>标注层条目：可行走覆盖 + 区域代价乘数。</summary>
    public struct NavBakeAnnotation
    {
        /// <summary>影响的世界 AABB 范围。</summary>
        public Aabb Region;

        /// <summary>可行走覆盖：0 = 不覆盖，1 = 强制可行走，2 = 强制阻挡。</summary>
        public byte WalkableOverride;

        /// <summary>区域代价乘数（1.0 = 默认，0.5 = 公路快，3.0 = 沼泽慢）。</summary>
        public float CostMultiplier;
    }
}
