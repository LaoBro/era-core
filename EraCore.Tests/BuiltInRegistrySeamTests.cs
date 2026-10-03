using System.Collections.Generic;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 声明式登记接缝（实现票 15）：
/// - 新增一条指令/函数 = 只写一条 descriptor（一处），funcDic / methodList 由其生成。
/// - descriptor 经**生产同一转换函数**（AddInstructionDescriptors / AddMethodDescriptors）产出可解析注册项。
/// 沿用既有回归套件的「内部 API 直调」模式（InternalsVisibleTo）。
///
/// 证据边界：端到端「真实 ERB 解析全绿」与逐位 surface 快照由实现票 17（面快照门禁）与票 25
/// 承担，不在此重复。此处只证明接缝本身是声明项的登记来源。
/// </summary>
public sealed class BuiltInRegistrySeamTests
{
	private sealed class DummyInstruction : AInstruction
	{
		public DummyInstruction()
		{
			ArgBuilder = ArgumentParser.GetArgumentBuilder(FunctionArgType.VOID);
			flag = FunctionIdentifier.METHOD_SAFE;
		}
	}

	private sealed class DummyMethod : FunctionMethod
	{
		public DummyMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = true;
		}
	}

	[Fact]
	public void Declared_entries_are_registered_by_the_seam()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		// 指令声明已含 Skia P0 动态调用 5 项（票 20）；函数声明已含数学/位/求值 19（票 18）+ MAP 6（票 19）。
		Assert.NotEmpty(BuiltInRegistry.Instructions);
		Assert.NotEmpty(BuiltInRegistry.Methods);
		var instructionDic = FunctionIdentifier.GetInstructionNameDic();
		foreach (InstructionDescriptor descriptor in BuiltInRegistry.Instructions)
		{
			Assert.Equal(Dialect.Snake, descriptor.Dialect);
			Assert.True(instructionDic.ContainsKey(descriptor.Name), descriptor.Name);
		}
		var methodList = FunctionMethodCreator.GetMethodList();
		foreach (FunctionMethodDescriptor descriptor in BuiltInRegistry.Methods)
		{
			Assert.Equal(Dialect.Snake, descriptor.Dialect);
			Assert.True(methodList.ContainsKey(descriptor.Name), descriptor.Name);
		}
	}

	[Fact]
	public void Descriptor_alone_registers_a_resolvable_instruction()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());
		var dic = FunctionIdentifier.CreateInstructionDictionary();

		FunctionIdentifier.AddInstructionDescriptors(dic,
		[
			new InstructionDescriptor
			{
				Name = "DIALECT_DUMMY_INSTRUCTION",
				CreateInstruction = () => new DummyInstruction(),
				AdditionalFlag = FunctionIdentifier.EXTENDED,
				Dialect = Dialect.Snake,
			},
		]);

		Assert.True(dic.ContainsKey("DIALECT_DUMMY_INSTRUCTION"));
		var entry = dic["DIALECT_DUMMY_INSTRUCTION"];
		Assert.Equal("DIALECT_DUMMY_INSTRUCTION", entry.Name);
		Assert.Equal(FunctionCode.__NULL__, entry.Code);
		Assert.NotNull(entry.ArgBuilder);
		Assert.NotNull(entry.Instruction);
		Assert.True(entry.IsMethodSafe());
		Assert.True(entry.IsExtended());
	}

	[Fact]
	public void Descriptor_alone_registers_a_method_with_its_signature()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());
		var methods = new Dictionary<string, FunctionMethod>(Config.StrComper);

		FunctionMethodCreator.AddMethodDescriptors(methods,
		[
			new FunctionMethodDescriptor
			{
				Name = "DIALECT_DUMMY_METHOD",
				CreateMethod = () => new DummyMethod(),
				Dialect = Dialect.Snake,
			},
		]);

		Assert.True(methods.ContainsKey("DIALECT_DUMMY_METHOD"));
		var method = methods["DIALECT_DUMMY_METHOD"];
		Assert.Equal(typeof(long), method.ReturnType);
	}

	[Fact]
	public void Existing_surface_is_untouched_by_the_seam()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());
		var dic = FunctionIdentifier.GetInstructionNameDic();

		Assert.True(dic.ContainsKey("TINPUT"));
		Assert.True(dic.ContainsKey("HTML_PRINT"));
		// 式中函数经静态构造器合并进 funcDic——迁移不得丢掉这条链路。
		Assert.True(dic.ContainsKey("GETCHARA"));
	}
}