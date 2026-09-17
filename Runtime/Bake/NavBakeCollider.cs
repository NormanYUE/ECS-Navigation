using Ember.Collision;

namespace Ember.Navigation
{
    /// <summary>烘焙碰撞体条目：形状 + 世界位姿 + 层 + 标志。</summary>
    public struct NavBakeCollider
    {
        /// <summary>碰撞体（形状与参数，复用 Ember.Collision 定义）。</summary>
        public Collider Collider;

        /// <summary>世界位姿。</summary>
        public BodyPose Pose;

        /// <summary>过滤层（标注层的参与掩码决定哪些层进烘焙）。</summary>
        public CollisionFilter Filter;

        /// <summary>标志位（bit0 = 静态）。</summary>
        public byte Flags;
    }
}
