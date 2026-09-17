using System.Threading;

namespace TileDownloader.Services
{
    /// <summary>
    /// 单个 pak 分块（512×512 瓦片）的存在性位图：
    /// 定长 32KB，替代原先 "z_x_y" 字符串存在性字典（同规模约 20MB/分块），
    /// 置位/清位用 Interlocked 保证并发落盘线程不丢位
    /// </summary>
    internal sealed class TileBlockBitmap
    {
        /// <summary>分块边长（瓦片数），与 pak 分块规则一致</summary>
        public const int Size = TileUrlBuilder.BlockSize;

        private const int Words = Size * Size / 64;

        private readonly long[] _words = new long[Words];

        /// <summary>瓦片绝对坐标 → 分块内位偏移（0 … 511*512+511）</summary>
        public static int Offset(int x, int y) => x % Size * Size + y % Size;

        /// <summary>置位（已存在）</summary>
        public void Set(int x, int y)
        {
            var offset = Offset(x, y);
            Interlocked.Or(ref _words[offset >> 6], 1L << (offset & 63));
        }

        /// <summary>清位（落盘最终失败时撤销标记，使续传能重新下载）</summary>
        public void Clear(int x, int y)
        {
            var offset = Offset(x, y);
            Interlocked.And(ref _words[offset >> 6], ~(1L << (offset & 63)));
        }

        /// <summary>查询是否已存在</summary>
        public bool Test(int x, int y)
        {
            var offset = Offset(x, y);
            return (_words[offset >> 6] & (1L << (offset & 63))) != 0;
        }
    }

    /// <summary>
    /// 分块存在性缓存（单文件 pak 的分表 / 多文件 pak 的分块文件共用）：
    /// - 矩形 [X0..X1]×[Y0..Y1] 为初始化时按任务范围预加载确认的区域，区域内未置位即确认不存在（无需再查库）
    /// - 位图懒分配：没有已存在瓦片的分块不占内存
    /// - 未落在确认矩形内的查询（理论上引擎不会发起）由调用方回源查库兜底
    /// </summary>
    internal sealed class PakBlockCache
    {
        private TileBlockBitmap? _bits;

        public PakBlockCache(int x0, int x1, int y0, int y1)
        {
            X0 = x0;
            X1 = x1;
            Y0 = y0;
            Y1 = y1;
        }

        /// <summary>已确认矩形（绝对瓦片坐标，闭区间）</summary>
        public int X0 { get; }
        public int X1 { get; }
        public int Y0 { get; }
        public int Y1 { get; }

        /// <summary>瓦片是否落在已确认矩形内</summary>
        public bool InRange(int x, int y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;

        /// <summary>登记为已存在（首次登记时分配位图）</summary>
        public void Set(int x, int y)
        {
            var bits = _bits;
            if (bits == null)
            {
                var created = new TileBlockBitmap();
                bits = Interlocked.CompareExchange(ref _bits, created, null) ?? created;
            }
            bits.Set(x, y);
        }

        /// <summary>撤销已存在标记</summary>
        public void Clear(int x, int y) => Volatile.Read(ref _bits)?.Clear(x, y);

        /// <summary>查询是否已存在</summary>
        public bool Test(int x, int y) => Volatile.Read(ref _bits)?.Test(x, y) == true;
    }
}
