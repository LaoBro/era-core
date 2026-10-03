namespace MinorShift.Emuera.GameData.Function;

/// <summary>
/// 位数组助手（实现票 18）：把一维整数数组视作 64 位字组成的位图，提供 Skia 方言
/// <c>BITSET</c> / <c>BITGET</c> / <c>BITTOGGLE</c> / <c>BITINDEXOFFIRST</c> 的语义。
///
/// 逐行对齐 Skia 参照（<c>Runtime/Utils/尊尼获加/BitArrayManager.cs</c>），
/// 仅去掉 era-core 不需要的 <c>SparseArray</c> 分支（spec §2.2 排除项）。
/// 与既有单整数 <c>GETBIT</c> / <c>SETBIT</c> 无关：这里操作的是数组位图。
/// </summary>
internal static class BitArrayManager
{
	/// <summary>BIT[i] = 第 i 位（1-based）的掩码；下标 0 不用，故长度 65。</summary>
	private static readonly long[] BIT;

	static BitArrayManager()
	{
		BIT = new long[65];
		for (int i = 1; i <= 64; i++)
			BIT[i] = 1L << (i - 1);
	}

	/// <summary>从 idx 起连续 length 位写入 val（非 0 置位，0 清位）；越界自动截断。</summary>
	public static void BitSet(long[] array, long idx, long val, long length)
	{
		if (array == null)
			return;
		long size = array.LongLength * 64;
		for (long i = 0; i < length; i++)
		{
			long index = idx + i;
			if (index >= size)
				break;
			long udx = index / 64;
			long j = index % 64 + 1;
			if (val != 0)
				array[udx] |= BIT[j];
			else
				array[udx] &= ~BIT[j];
		}
	}

	/// <summary>读第 idx 位；越界或 null 返回 -1。</summary>
	public static long BitGet(long[] array, long idx)
	{
		if (array == null)
			return -1;
		long size = array.LongLength * 64;
		if (idx >= size || idx < 0)
			return -1;
		long udx = idx / 64;
		idx = idx % 64 + 1;
		return (array[udx] & BIT[idx]) != 0 ? 1 : 0;
	}

	/// <summary>翻转第 idx 位；越界或 null 返回 0，正常返回 1。</summary>
	public static long BitToggle(long[] array, long idx)
	{
		if (array == null)
			return 0;
		long size = array.LongLength * 64;
		if (idx >= size || idx < 0)
			return 0;
		long udx = idx / 64;
		idx = idx % 64 + 1;
		array[udx] ^= BIT[idx];
		return 1;
	}

	/// <summary>返回首个等于 val 语义的位下标（val != 0 找置位，val == 0 找清位）；无则 -1。</summary>
	public static long BitIndexOfFirst(long[] array, long val)
	{
		if (array == null)
			return -1;
		bool searchForSet = val != 0;
		long arraySize = array.LongLength;
		long word = 0;
		while (word < arraySize && array[word] == (searchForSet ? 0 : -1))
			word++;
		for (long i = 0; i < 64; i++)
		{
			long bitVal = BitGet(array, word * 64 + i);
			if (bitVal == -1)
				return -1;
			if ((bitVal == 1) == searchForSet)
				return word * 64 + i;
		}
		return -1;
	}
}