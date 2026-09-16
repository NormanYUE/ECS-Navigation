using Ember;
using UnityEngine;

namespace Ember.Navigation
{
    /// <summary>
    /// 把烘焙产物资产读进导航世界的桥接组件。
    ///
    /// 挂在驱动 <see cref="ECSManager"/> 的同一个 GameObject 上，在 <see cref="Start"/>
    /// 里加载一次 —— 必须早于任何导航系统运行：段缓冲的建立会搬移地址，
    /// 在途 Job 持有旧指针会读到已释放内存。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NavBakedAssetLoader : MonoBehaviour
    {
        /// <summary>驱动 ECS 的管理器（留空则取同物体上的实例）。</summary>
        [SerializeField] private ECSManager m_Manager;

        /// <summary>烘焙产物。</summary>
        [SerializeField] private NavBakedAsset m_Asset;

        /// <summary>加载是否成功（未挂资产时为 false）。</summary>
        public bool Loaded { get; private set; }

        private void Awake()
        {
            if (m_Manager == null) m_Manager = GetComponent<ECSManager>();
        }

        private void Start()
        {
            if (m_Manager == null || m_Manager.World == null) return;
            if (m_Asset == null) return;

            Loaded = m_Asset.LoadInto(m_Manager.World);
            if (!Loaded)
                Debug.LogError($"[{nameof(NavBakedAssetLoader)}] 导航数据加载失败：资产为空或格式校验不通过。", this);
        }

        /// <summary>运行期换图：校验后热替换。</summary>
        public bool Reload()
        {
            if (m_Manager == null || m_Manager.World == null || m_Asset == null) return false;
            Loaded = m_Asset.LoadInto(m_Manager.World);
            return Loaded;
        }
    }
}
