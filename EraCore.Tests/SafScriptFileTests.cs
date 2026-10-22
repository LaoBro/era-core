using MinorShift.Emuera.Files;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

[Collection("LoaderTests")]
public class SafScriptFileTests
{
	private const string Root =
		"content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FTK";

    [Fact]
	public void Relative_script_path_in_sav_tree_resolves_and_creates_each_parent_directory()
	{
		var accessor = new InMemoryContentDirAccessor(Root);
		using var gamePaths = new RootScope(Root, accessor);

        var resolved = SafCompat.TryResolveGameRelativePath("sav/profiles/note.txt", createParentDirectories: true, out var path);

        Assert.True(resolved);
        var savDir = SafCompat.ResolveSubPath(Root, "sav");
        var profilesDir = SafCompat.ResolveSubPath(savDir, "profiles");
        Assert.True(accessor.DirectoryExists(savDir));
        Assert.True(accessor.DirectoryExists(profilesDir));
		Assert.Equal("note.txt", SafPath.GetLogicalFileName(path));
	}

	[Theory]
	[InlineData("root.txt", "root.txt")]
	[InlineData(@"sav\profiles\note.txt", "note.txt")]
	public void Relative_script_path_allows_root_files_and_backslash_separators(string relativePath, string expectedFileName)
	{
		var accessor = new InMemoryContentDirAccessor(Root);
		using var gamePaths = new RootScope(Root, accessor);

		Assert.True(SafCompat.TryResolveGameRelativePath(relativePath, createParentDirectories: true, out var path));
		Assert.Equal(expectedFileName, SafPath.GetLogicalFileName(path));
	}

	/// <summary>
	/// 任意游戏根子目录都可解析（<c>bad/</c>、<c>plugins/</c>、<c>erb/</c>、多级 <c>assets/img/</c>…）。
	/// 旧实现只放行根目录与 <c>sav/</c>，其余一律 <c>false</c>——eraTW <c>@BADAPPLE</c> 的
	/// <c>LOADTEXT("bad/bad_apple{count}.txt")</c> 因此静默返回空串，动画全程画不出内容。
	/// 越界防护见 <see cref="Relative_script_path_rejects_unsafe_or_unsupported_locations"/>。
	/// </summary>
	[Theory]
	[InlineData("bad/bad_apple1.txt", "bad_apple1.txt")]
	[InlineData("plugins/bbas_dataset.xml", "bbas_dataset.xml")]
	[InlineData("erb/script.txt", "script.txt")]
	[InlineData(@"assets\img\a.txt", "a.txt")]
	public void Relative_script_path_allows_any_descendant_of_game_root(string relativePath, string expectedFileName)
	{
		var accessor = new InMemoryContentDirAccessor(Root);
		using var gamePaths = new RootScope(Root, accessor);

		// 与参考实现 Utils.GetValidPath 对齐：不按目录名设白名单，只挡越界。
		Assert.True(SafCompat.TryResolveGameRelativePath(relativePath, createParentDirectories: true, out var path));
		Assert.Equal(expectedFileName, SafPath.GetLogicalFileName(path));
	}

	/// <summary>
	/// eraTW <c>TITLE.ERB</c> 的 <c>@BADAPPLE</c> 用 <c>LOADTEXT("bad/bad_apple{count}.txt")</c>
	/// 逐帧读 6572 个 ASCII 动画文件。路径白名单曾只放行 <c>sav/</c>，这些调用全部静默返回空串，
	/// 动画画不出任何内容——表现为「点 [2] DEMO 后界面冻住」。此处钉住 <c>bad/</c> 可读。
	/// </summary>
	[Fact]
	public void LoadText_reads_animation_frames_outside_sav_tree()
	{
		var accessor = new InMemoryContentDirAccessor(Root);
		using var gamePaths = new RootScope(Root, accessor);
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		var badDir = SafCompat.ResolveSubPath(Root, "bad");
		SafCompat.CreateDirectory(badDir);
		accessor.Seed(SafCompat.CombinePath(badDir, "bad_apple1.txt"), "@@@@\n@@@@\n"u8.ToArray());

		var loadText = FunctionMethodCreator.GetMethodList()["LOADTEXT"];
		Assert.Equal("@@@@\n@@@@\n", loadText.GetStrValue(null!, [new SingleStrTerm("bad/bad_apple1.txt")]));
	}

    [Theory]
    [InlineData("")]
    [InlineData("../escape.txt")]
    [InlineData("sav/../escape.txt")]
    [InlineData("bad/../../escape.txt")]
    [InlineData("sav//empty.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData("./relative.txt")]
	public void Relative_script_path_rejects_unsafe_or_unsupported_locations(string input)
	{
		var accessor = new InMemoryContentDirAccessor(Root);
		using var gamePaths = new RootScope(Root, accessor);

        Assert.False(SafCompat.TryResolveGameRelativePath(input, createParentDirectories: false, out _));
    }

    [Fact]
	public void GetDatFiles_returns_logical_suffixes_for_content_uris()
	{
		var accessor = new InMemoryContentDirAccessor(Root);
		using var gamePaths = new RootScope(Root, accessor);
        var datDir = SafCompat.ResolveSubPath(Root, "dat");
        SafCompat.CreateDirectory(datDir);
        accessor.Seed(SafCompat.CombinePath(datDir, "var_01.dat"), [0x01]);
        accessor.Seed(SafCompat.CombinePath(datDir, "var_custom.dat"), [0x02]);
        accessor.Seed(SafCompat.CombinePath(datDir, "chara_alice.dat"), [0x03]);

        Assert.Equal(["01", "custom"], VariableEvaluator.GetDatFiles(charadat: false, "*").OrderBy(value => value));
        Assert.Equal(["alice"], VariableEvaluator.GetDatFiles(charadat: true, "*").OrderBy(value => value));
    }

    [Fact]
	public void SaveText_and_LoadText_support_content_uri_string_and_number_paths()
	{
		var accessor = new InMemoryContentDirAccessor(Root);
		using var gamePaths = new RootScope(Root, accessor);
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var saveText = FunctionMethodCreator.GetMethodList()["SAVETEXT"];
        var loadText = FunctionMethodCreator.GetMethodList()["LOADTEXT"];
        var originalSaveEncoding = Config.SaveEncode;
        Config.SaveEncode = EncodingHandler.shiftjisEncoding;
        try
        {
            Assert.Equal(1, saveText.GetIntValue(null!,
            [
                new SingleStrTerm("UTF-8 text"), new SingleStrTerm("sav/profiles/note"),
                new SingleLongTerm(0), new SingleLongTerm(1),
            ]));

            Assert.True(SafCompat.TryResolveGameRelativePath("sav/profiles/note.txt", false, out var namedPath));
            Assert.True(accessor.FileExists(namedPath));
			Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, accessor.ReadAllBytes(namedPath)![..3]);
			Assert.Equal("UTF-8 text", loadText.GetStrValue(null!, [new SingleStrTerm("sav/profiles/note.txt")]));
			Assert.True(SafCompat.TryResolveGameRelativePath("sav/no-bom.txt", false, out var noBomPath));
			accessor.Seed(noBomPath, EncodingHandler.UTF8Encoding.GetBytes("no BOM UTF-8: 日本語"));
			Assert.Equal("no BOM UTF-8: 日本語", loadText.GetStrValue(null!, [new SingleStrTerm("sav/no-bom.txt")]));

			Assert.Equal(1, saveText.GetIntValue(null!, [new SingleStrTerm("番号"), new SingleLongTerm(7)]));
            var numberedPath = SafCompat.CombinePath(Root, "txt07.txt");
            Assert.True(accessor.FileExists(numberedPath));
            Assert.Equal(EncodingHandler.shiftjisEncoding.GetBytes("番号"), accessor.ReadAllBytes(numberedPath));
            Assert.Equal("番号", loadText.GetStrValue(null!, [new SingleLongTerm(7)]));
        }
        finally
        {
            Config.SaveEncode = originalSaveEncoding;
        }
    }

	[Fact]
	public void SaveText_and_LoadText_share_relative_extension_normalization()
	{
		var accessor = new InMemoryContentDirAccessor(Root);
		using var gamePaths = new RootScope(Root, accessor);
		using var scope = GlobalStatic.OpenScope(new ConfigData());
		var saveText = FunctionMethodCreator.GetMethodList()["SAVETEXT"];
		var loadText = FunctionMethodCreator.GetMethodList()["LOADTEXT"];

		Assert.Equal(1, saveText.GetIntValue(null!,
		[
			new SingleStrTerm("shared"), new SingleStrTerm("sav/profiles/shared"),
			new SingleLongTerm(0), new SingleLongTerm(1),
		]));
		Assert.Equal("shared", loadText.GetStrValue(null!, [new SingleStrTerm("sav/profiles/shared")]));
	}

}
