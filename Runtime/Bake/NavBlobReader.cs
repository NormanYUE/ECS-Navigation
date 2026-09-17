using System;
using Ember.Collision;

namespace Ember.Navigation
{
    /// <summary>
    /// blob 读取与校验：校验魔数 / 版本 / 维度，暴露各段视图。
    /// 加载方在拆段写入 World 托管 buffer 前先走 <see cref="Validate"/>。
    /// </summary>
    public static unsafe class NavBlobReader
    {
        /// <summary>校验结果。</summary>
        public enum Status
        {
            /// <summary>校验通过。</summary>
            Ok = 0,

            /// <summary>魔数不匹配（非导航 blob）。</summary>
            BadMagic = 1,

            /// <summary>版本不匹配。</summary>
            BadVersion = 2,

            /// <summary>维度不匹配。</summary>
            BadDimension = 3,
        }

        /// <summary>读取头部（调用方保证 buffer 长度 ≥ sizeof(NavBlobHeader)）。</summary>
        public static NavBlobHeader ReadHeader(byte* blob) => *(NavBlobHeader*)blob;

        /// <summary>校验 blob 头部。</summary>
        /// <param name="blob">blob 起始指针。</param>
        /// <param name="expectedDimension">期望维度（不匹配即拒绝）。</param>
        public static Status Validate(byte* blob, CollisionDimension expectedDimension)
        {
            var header = ReadHeader(blob);
            if (header.MagicValue != NavBlobHeader.Magic) return Status.BadMagic;
            if (header.Version != NavBlobHeader.CurrentVersion) return Status.BadVersion;
            if (header.Dimension != expectedDimension) return Status.BadDimension;
            return Status.Ok;
        }

        /// <summary>取段数据指针（不做范围校验，调用方保证头部已 Validate）。</summary>
        public static byte* Segment(byte* blob, NavBlobSegment segment)
        {
            var header = ReadHeader(blob);
            return blob + header.SegmentOffsets[(int)segment];
        }

        /// <summary>取段字节长度。</summary>
        public static long SegmentLength(byte* blob, NavBlobSegment segment)
        {
            var header = ReadHeader(blob);
            return header.SegmentLengths[(int)segment];
        }

        /// <summary>从头部重建网格描述。</summary>
        public static NavGrid GridOf(in NavBlobHeader header) => new()
        {
            Origin = header.Origin,
            VoxelSize = header.VoxelSize,
            Dimensions = header.Dimensions,
            TileSize = header.TileSize,
        };
    }
}
