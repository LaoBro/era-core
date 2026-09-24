using System;
using System.Collections.Generic;

namespace MinorShift.Emuera.Runtime.Config;

/// <summary>
/// 配置键别名表——其它本地化分支写出的 emuera.config 键名 → 本引擎配置项。
///
/// <para>
/// 背景：<see cref="ConfigData.GetConfigItem(string)"/> 只认三类键名：<c>Name</c>（ConfigCode 名）、
/// <c>Text</c>（日文）、<c>EngText</c>（英文）。简体中文分支（Emuera1821 cn / uEmuera / gEmuera 一脉）
/// 写出的 emuera.config 用中文键名，例如「忽略大小写」「在sav文件夹中创建存档」——与本引擎的
/// 三类键名都不同，于是这些键读不到、值回落默认。这正是「EraCore 打开一次后原版 Emuera 满屏报错」
/// 事故的成因之一（写回问题已另行修掉，此处修的是读侧）。
/// </para>
/// <para>
/// 注意：<c>lang/emuera-zhs.xml</c> <b>不能</b>用来推导这张表——它存的是 UI 标签
/// （「使用鼠标」「将存档保存到sav文件夹」「历史日志行数」），与配置<b>文件</b>里的键名
/// （「启用鼠标」「在sav文件夹中创建存档」「记录日志的行数」）是两套不同字符串（已逐条比对）。
/// </para>
/// <para>
/// 数据可复核、可重生成：来源是 gEmuera 的成对键名表（97 行日文 + 97 行中文，逐行配对）。
/// 生成规则 = 取日文名在本引擎有同名配置项的行、且中文名≠日文名。想重新推导就照 §Map 上方的注释做。
/// </para>
/// <para>
/// 校验：ConfigKeyAliasesTests 断言 ①别名非空；②每条别名都能解析到真实配置项；
/// ③不与任何 Name/Text/EngText 冲突；④一个别名不被两个 ConfigCode 同时声明。
/// </para>
/// </summary>
internal static class ConfigKeyAliases
{
	/// <summary>ConfigCode → 该 Code 额外接受的键名（除 Name/Text/EngText 之外）。</summary>
	private static readonly Dictionary<ConfigCode, string[]> Map = new()
	{
		// 数据来源：gEmuera 的成对键名表（assets/text/emuera_config_utf8.txt 与 _zhcn.txt，
		// 各 97 行、逐行配对）。取「日文名在本引擎中存在同名配置项」的行，CN 名与 JP 名不同者即为别名。
		// 97 行里 96 行按日文名精确命中本引擎配置项（唯一未命中的「無限ループ警告までのミリ秒数」
		// 本分支已移除），故每一条都有精确的日文名锚，不是猜测。行尾注释即该锚，便于复核。
		// 来源：https://github.com/wwwXiaoHan17/gEmuera （uEmuera/XEmuera 同源，未做中文键名适配）
		[ConfigCode.IgnoreCase] = ["忽略大小写"], // 大文字小文字の違いを無視する
		[ConfigCode.UseRenameFile] = ["启用_Rename.csv"], // _Rename.csvを利用する
		[ConfigCode.UseReplaceFile] = ["启用_Replace.csv"], // _Replace.csvを利用する
		[ConfigCode.UseMouse] = ["启用鼠标"], // マウスを使用する
		[ConfigCode.UseMenu] = ["显示菜单栏"], // メニューを使用する
		[ConfigCode.UseDebugCommand] = ["启用调试命令"], // デバッグコマンドを使用する
		[ConfigCode.AllowMultipleInstances] = ["允许多重启动"], // 多重起動を許可する
		[ConfigCode.AutoSave] = ["自动保存进度"], // オートセーブを行なう
		[ConfigCode.UseKeyMacro] = ["启用键盘宏"], // キーボードマクロを使用する
		[ConfigCode.SizableWindow] = ["允许调整窗口高度"], // ウィンドウの高さを可変にする
		[ConfigCode.TextDrawingMode] = ["绘图界面"], // 描画インターフェース
		[ConfigCode.WindowX] = ["窗口宽度"], // ウィンドウ幅
		[ConfigCode.WindowY] = ["窗口高度"], // ウィンドウ高さ
		[ConfigCode.WindowPosX] = ["窗口X坐标"], // ウィンドウ位置X
		[ConfigCode.WindowPosY] = ["窗口Y坐标"], // ウィンドウ位置Y
		[ConfigCode.SetWindowPos] = ["启动时固定窗口位置"], // 起動時のウィンドウ位置を指定する
		[ConfigCode.WindowMaximixed] = ["启动时将窗口最大化"], // 起動時にウィンドウを最大化する
		[ConfigCode.MaxLog] = ["记录日志的行数"], // 履歴ログの行数
		[ConfigCode.PrintCPerLine] = ["PRINTC并列数量"], // PRINTCを並べる数
		[ConfigCode.PrintCLength] = ["PRINTC文字数量"], // PRINTCの文字数
		[ConfigCode.FontName] = ["字体名称"], // フォント名
		[ConfigCode.FontSize] = ["字体大小"], // フォントサイズ
		[ConfigCode.LineHeight] = ["每行高度"], // 一行の高さ
		[ConfigCode.FPS] = ["每秒帧数"], // フレーム毎秒
		[ConfigCode.SkipFrame] = ["最大SKIP帧数"], // 最大スキップフレーム数
		[ConfigCode.ScrollHeight] = ["滚动行数"], // スクロール行数
		[ConfigCode.DisplayWarningLevel] = ["最低显示警告等级"], // 表示する最低警告レベル
		[ConfigCode.DisplayReport] = ["加载时显示报告"], // ロード時にレポートを表示する
		[ConfigCode.ReduceArgumentOnLoad] = ["加载时解析参数"], // ロード時に引数を解析する
		[ConfigCode.IgnoreUncalledFunction] = ["忽略未被调用过的函数"], // 呼び出されなかった関数を無視する
		[ConfigCode.FunctionNotFoundWarning] = ["函数未找到时的警告处理"], // 関数が見つからない警告の扱い
		[ConfigCode.FunctionNotCalledWarning] = ["函数未被调用时的警告处理"], // 関数が呼び出されなかった警告の扱い
		[ConfigCode.ButtonWrap] = ["不对按钮行折返换行"], // ボタンの途中で行を折りかえさない
		[ConfigCode.SearchSubdirectory] = ["检索子目录"], // サブディレクトリを検索する
		[ConfigCode.SortWithFilename] = ["按文件名顺序读取"], // 読み込み順をファイル名順にソートする
		[ConfigCode.SaveDataNos] = ["使用存档数量"], // 表示するセーブデータ数
		[ConfigCode.WarnBackCompatibility] = ["显示eramaker兼容性相关警告"], // eramaker互換性に関する警告を表示する
		[ConfigCode.AllowFunctionOverloading] = ["允许重写系统函数"], // システム関数の上書きを許可する
		[ConfigCode.WarnFunctionOverloading] = ["重写系统函数时显示警告"], // システム関数が上書きされたとき警告を表示する
		[ConfigCode.TextEditor] = ["关联文本编辑器"], // 関連づけるテキストエディタ
		[ConfigCode.EditorType] = ["编辑器运行参数"], // テキストエディタコマンドライン指定
		[ConfigCode.EditorArgument] = ["编辑器运行参数值"], // エディタに渡す行指定引数
		[ConfigCode.WarnNormalFunctionOverloading] = ["重复定义非事件函数时显示警告"], // 同名の非イベント関数が複数定義されたとき警告する
		[ConfigCode.CompatiErrorLine] = ["执行未能解析的行"], // 解釈不可能な行があっても実行する
		[ConfigCode.CompatiCALLNAME] = ["CALLNAME空字符串时代入NAME"], // CALLNAMEが空文字列の時にNAMEを代入する
		[ConfigCode.UseSaveFolder] = ["在sav文件夹中创建存档"], // セーブデータをsavフォルダ内に作成する
		[ConfigCode.CompatiRAND] = ["伪变量RAND符合eramaker规范"], // 擬似変数RANDの仕様をeramakerに合わせる
		[ConfigCode.CompatiFunctionNoignoreCase] = ["函数、属性大小写敏感"], // 関数・属性については大文字小文字を無視しない
		[ConfigCode.SystemAllowFullSpace] = ["使用全角空格填充空白区域"], // 全角スペースをホワイトスペースに含める
		[ConfigCode.CompatiLinefeedAs1739] = ["重现ver1739前的非按钮折返"], // ver1739以前の非ボタン折り返しを再現する
		[ConfigCode.useLanguage] = ["内部使用东亚语言"], // 内部で使用する東アジア言語
		[ConfigCode.CompatiCallEvent] = ["允许CALL调用事件函数"], // イベント関数のCALLを許可する
		[ConfigCode.CompatiSPChara] = ["使用SP角色"], // SPキャラを使用する
		[ConfigCode.SystemSaveInBinary] = ["以二进制形式保存存档"], // セーブデータをバイナリ形式で保存する
		[ConfigCode.CompatiFuncArgOptional] = ["用户函数允许省略全部参数"], // ユーザー関数の全ての引数の省略を許可する
		[ConfigCode.CompatiFuncArgAutoConvert] = ["用户参数自动补充TOSTR"], // ユーザー関数の引数に自動的にTOSTRを補完する
		[ConfigCode.SystemIgnoreTripleSymbol] = ["展开FORM中的三连记号"], // FORM中の三連記号を展開しない
		[ConfigCode.TimesNotRigorousCalculation] = ["TIMES的计算符合eramaker规范"], // TIMESの計算をeramakerにあわせる
		[ConfigCode.SystemNoTarget] = ["禁用角色变量的参数自动补全"], // キャラクタ変数の引数を補完しない
	};

	/// <summary>全部别名声明（构建别名索引时投影，见 <see cref="ConfigData.BuildIndex"/>）。</summary>
	internal static IEnumerable<KeyValuePair<ConfigCode, string[]>> All => Map;
}