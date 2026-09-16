using System.IO;
using Ember;
using Ember.Collision;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
using Unity.Mathematics;

namespace Ember.Navigation.Editor
{
    /// <summary>
    /// 导航烘焙窗口。
    ///
    /// 从<b>运行中的碰撞世界</b>取静态碰撞体烘焙，而不是在编辑模式另扫一遍场景：
    /// 碰撞体在 ECS 世界里，编辑模式没有这个世界；重扫一遍既是重复实现，
    /// 也会和碰撞模块的口径分叉（层过滤、顶点池、缩放语义）。
    /// 因此烘焙在 Play 模式进行，产物存成 <see cref="NavBakedAsset"/> 供运行时加载。
    ///
    /// 烘焙参数与 <see cref="NavConfig"/> 对齐 —— 两处不一致会让运行期求解器
    /// 按错误的连通度展开邻居。
    /// </summary>
    public sealed class NavBakeWindow : EditorWindow
    {
        private NavBakeInput m_Input = new()
        {
            Dimension = CollisionDimension.XY,
            VoxelSize = 0.5f,
            TileSize = 32,
            DistanceBits = 8,
            MaxBakeRadius = 8f,
            Connectivity = 4,
        };

        private int m_ColliderCapacity = 4096;
        private string m_OutputPath = "Assets/NavBakedData.asset";
        private string m_Status = "等待烘焙。";

        [MenuItem("Ember/Navigation/烘焙窗口")]
        public static void Open() =>
            GetWindow<NavBakeWindow>("导航烘焙").minSize = new Vector2(420f, 320f);

        private void OnGUI()
        {
            EditorGUILayout.LabelField("烘焙参数", EditorStyles.boldLabel);
            m_Input.Dimension = (CollisionDimension)EditorGUILayout.EnumPopup("维度", m_Input.Dimension);
            m_Input.VoxelSize = EditorGUILayout.FloatField("体素边长（米）", m_Input.VoxelSize);
            m_Input.TileSize = EditorGUILayout.IntField("tile 边长（体素）", m_Input.TileSize);
            m_Input.DistanceBits = (byte)EditorGUILayout.IntPopup("距离场位宽", m_Input.DistanceBits,
                new[] { "8", "16" }, new[] { 8, 16 });
            m_Input.MaxBakeRadius = EditorGUILayout.FloatField("烘焙半径上限（米）", m_Input.MaxBakeRadius);
            m_Input.Connectivity = (byte)EditorGUILayout.IntPopup("连通度", m_Input.Connectivity,
                new[] { "4（2D 四邻）", "8（2D 八邻）", "6（3D 六邻）", "26（3D 廿六邻）" },
                new[] { 4, 8, 6, 26 });

            EditorGUILayout.Space();
            m_ColliderCapacity = EditorGUILayout.IntField("碰撞体容量上界", m_ColliderCapacity);
            m_OutputPath = EditorGUILayout.TextField("产物路径", m_OutputPath);

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(m_Status, MessageType.None);

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("烘焙并保存")) BakeAndSave();
            }

            if (!Application.isPlaying)
                EditorGUILayout.HelpBox("需要进入 Play 模式：碰撞世界只在运行时存在。", MessageType.Info);
        }

        private unsafe void BakeAndSave()
        {
            ECSManager manager = NavBakeContext.Manager;
            if (manager == null || manager.World == null)
            {
                m_Status = "未拿到正在运行的 ECSManager：" +
                    "请在启动代码里设置 NavBakeContext.Manager（一行）。";
                return;
            }

            using var colliders = new NativeArray<NavBakeCollider>(
                math.max(m_ColliderCapacity, 1), Allocator.Temp);

            int count = NavRuntimeBake.GatherColliders(
                manager.World, staticOnly: true,
                (NavBakeCollider*)colliders.GetUnsafePtr(), colliders.Length);
            if (count < 0)
            {
                m_Status = $"碰撞体超过容量上界 {colliders.Length}，请调大后重试。";
                return;
            }

            // 顶点池直接借用碰撞快照的视图（多边形顶点在碰撞模块维护）。
            float3* vertexPool = null;
            int vertexPoolLength = 0;
            if (manager.World.TryGetCollisionWorld(out CollisionWorldView collision)
                && collision.IsQueryReady && collision.VertexPool.Length > 0)
            {
                vertexPool = (float3*)collision.VertexPool.GetUnsafePtr();
                vertexPoolLength = collision.VertexPool.Length;
            }

            using var workspace = new NavRuntimeBakeWorkspace();
            long bytes = NavRuntimeBake.Bake(m_Input, (NavBakeCollider*)colliders.GetUnsafePtr(), count,
                vertexPool, vertexPoolLength, workspace);
            if (bytes <= 0)
            {
                m_Status = "烘焙失败：计划阶段未产出有效 blob。";
                return;
            }

            var blob = new byte[bytes];
            NativeArray<byte>.Copy(workspace.Blob, blob, (int)bytes);

            NavBakedAsset asset = LoadOrCreate(m_OutputPath);
            if (asset == null) return;

            asset.SetData(blob, m_Input.Dimension, m_Input.VoxelSize);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            m_Status = $"已烘焙 {count} 个静态碰撞体，blob {bytes} 字节 → {m_OutputPath}";
        }

        private NavBakedAsset LoadOrCreate(string path)
        {
            NavBakedAsset asset = AssetDatabase.LoadAssetAtPath<NavBakedAsset>(path);
            if (asset != null) return asset;

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            asset = CreateInstance<NavBakedAsset>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }
    }
}
