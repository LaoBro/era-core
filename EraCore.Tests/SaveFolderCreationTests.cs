using MinorShift.Emuera.Files;
using System;
using System.IO;
using MinorShift.Emuera.Runtime.Config;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0038 行为锁（先钉后改）：启用「在 sav 文件夹中创建存档」后的实际行为——
/// <b>创建 sav 目录</b>，但旧存档（<c>global.sav</c> / <c>save*.sav</c>）<b>不被搬移</b>：
/// 搬移前有一道 <c>Dialog.ShowPrompt</c> 同意提示，而无头实现恒返回 No
///（<c>HeadlessDialog.ShowPrompt</c>，日志 "auto-select: No"）。
/// <para>
/// 本测试在 ADR-0038 的<b>改动之前</b>就应为绿，改动之后仍须为绿——它钉的是"不搬移"这个
/// 刻意保持不变的行为，防止后续有人"顺手"把提示改成默认同意。
/// </para>
/// </summary>
public class SaveFolderCreationTests
{
	[Fact]
	public void Save_folder_is_created_and_existing_saves_are_left_in_place()
	{
		var root = Path.Combine(Path.GetTempPath(), "emuera-sav-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		using var gameRoot = new RootScope(root, new FileSystemGameDirAccessor());
		try
		{
			File.WriteAllText(Path.Combine(root, "global.sav"), "G");
			File.WriteAllText(Path.Combine(root, "save01.sav"), "S1");

			var config = new ConfigData(gameRoot.Paths);
			config.GetConfigItem(ConfigCode.UseSaveFolder).SetValue(true);
			config.ApplyPostLoadEffects(gameRoot.Paths.DirAccessor);

			var sav = Path.Combine(root, "sav");
			Assert.True(Directory.Exists(sav), "启用 sav 文件夹时应创建 sav 目录");
			Assert.True(File.Exists(Path.Combine(root, "global.sav")),
				"旧存档不应被搬移：无头下同意提示恒为 No");
			Assert.True(File.Exists(Path.Combine(root, "save01.sav")),
				"旧存档不应被搬移：无头下同意提示恒为 No");
			Assert.False(File.Exists(Path.Combine(sav, "global.sav")));
			Assert.False(File.Exists(Path.Combine(sav, "save01.sav")));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { /* 清理失败不掩盖断言 */ }
		}
	}
}
