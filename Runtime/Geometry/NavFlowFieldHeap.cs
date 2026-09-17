namespace Ember.Navigation
{
    /// <summary>
    /// 二叉最小堆（裸指针、零分配），Dijkstra 波前用。
    /// 元素 = (float 代价, int 体素下标)；同代价按下标升序弹出，保证确定性。
    /// 调用方提供堆数组（容量 ≥ 最大波前宽度；越界由调用方预算控制）。
    /// </summary>
    public static unsafe class NavFlowFieldHeap
    {
        /// <summary>入堆。返回 false 表示堆满（调用方需扩容或降预算）。</summary>
        public static bool Push(float* costs, int* voxels, int capacity, ref int count,
            float cost, int voxel)
        {
            if (count >= capacity) return false;

            int i = count++;
            costs[i] = cost;
            voxels[i] = voxel;

            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (Compare(costs[parent], voxels[parent], costs[i], voxels[i]) <= 0) break;
                Swap(costs, voxels, i, parent);
                i = parent;
            }
            return true;
        }

        /// <summary>弹出最小元。count 为 0 时返回 false。</summary>
        public static bool Pop(float* costs, int* voxels, ref int count,
            out float cost, out int voxel)
        {
            cost = default;
            voxel = default;
            if (count <= 0) return false;

            cost = costs[0];
            voxel = voxels[0];
            count--;

            costs[0] = costs[count];
            voxels[0] = voxels[count];

            int i = 0;
            for (;;)
            {
                int left = i * 2 + 1;
                int right = left + 1;
                int smallest = i;

                if (left < count
                    && Compare(costs[left], voxels[left], costs[smallest], voxels[smallest]) < 0)
                    smallest = left;
                if (right < count
                    && Compare(costs[right], voxels[right], costs[smallest], voxels[smallest]) < 0)
                    smallest = right;
                if (smallest == i) break;

                Swap(costs, voxels, i, smallest);
                i = smallest;
            }
            return true;
        }

        private static int Compare(float costA, int voxelA, float costB, int voxelB)
        {
            int byCost = costA.CompareTo(costB);
            return byCost != 0 ? byCost : voxelA.CompareTo(voxelB);
        }

        private static void Swap(float* costs, int* voxels, int a, int b)
        {
            (costs[a], costs[b]) = (costs[b], costs[a]);
            (voxels[a], voxels[b]) = (voxels[b], voxels[a]);
        }
    }
}
