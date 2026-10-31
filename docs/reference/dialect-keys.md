# 方言键索引（生成物）

> 本文件由 `EraCore.Tests/DialectKeyIndexTests.cs` 生成，请勿手工编辑。
> 重新生成：设 `ERACORE_DIALECT_SURFACE_UPDATE=1` 运行该测试（与面快照共用开关）。

方言键行来自 descriptor 的 `Family` / `Status` / `Note`；baseline 行不含元数据，处理体列给出运行期类型（结构化指令没有 `AInstruction` 实例时退化为 `FunctionCode` 派发名）。

## 方言键

| 键 | kind | 模块 | 家族 | 状态 | 说明 |
|---|---|---|---|---|---|
| `ACOS` | function | snake | 数学 | Semantic | — |
| `ASIN` | function | snake | 数学 | Semantic | — |
| `ATAN` | function | snake | 数学 | Semantic | — |
| `BITGET` | function | snake | 位运算 | Semantic | — |
| `BITINDEXOFFIRST` | function | snake | 位运算 | Semantic | — |
| `BITSET` | function | snake | 位运算 | Semantic | — |
| `BITTOGGLE` | function | snake | 位运算 | Semantic | — |
| `CALLSTR` | directive | snake | 动态调用 | Semantic | — |
| `CEIL` | function | snake | 数学 | Semantic | — |
| `CLEARIMAGELAYER` | directive | snake | 图像层 | AcceptedNoOp | 参数解析；控制台 no-op |
| `CLEARIMAGELAYER_ALL` | directive | snake | 图像层 | AcceptedNoOp | 控制台 no-op；不渲染图像层 |
| `COS` | function | snake | 数学 | Semantic | — |
| `EVAL` | function | snake | 字符串求值 | Semantic | — |
| `EVALS` | function | snake | 字符串求值 | Semantic | — |
| `FLOOR` | function | snake | 数学 | Semantic | — |
| `GETANIMETIMER` | function | snake | 平台定时器 | Semantic | — |
| `GETPLATFORM` | function | snake | 平台定时器 | Semantic | — |
| `HTML_PRINTC` | directive | snake | HTML 文本 | Semantic | 复用 HTML 管线，单元格右对齐（已知差异） |
| `HTML_PRINTLC` | directive | snake | HTML 文本 | Semantic | 复用 HTML 管线，单元格左对齐（已知差异） |
| `JUMPSTR` | directive | snake | 动态调用 | Semantic | — |
| `MAP_FINDKEY` | function | snake | MAP | Semantic | — |
| `MAP_FROMSTRING` | function | snake | MAP | Semantic | — |
| `MAP_MERGE` | function | snake | MAP | Semantic | — |
| `MAP_REMOVEIF` | function | snake | MAP | Semantic | — |
| `MAP_TOSTRING` | function | snake | MAP | Semantic | — |
| `MAP_VALUES` | function | snake | MAP | Semantic | — |
| `ROUND` | function | snake | 数学 | Semantic | — |
| `SETIMAGELAYER` | directive | snake | 图像层 | AcceptedNoOp | 参数解析 + ColorMatrix 5×5 校验；控制台 no-op |
| `SETIMAGELAYERL` | directive | snake | 图像层 | AcceptedNoOp | 参数解析 + ColorMatrix 5×5 校验；followScroll 恒 true；no-op |
| `SIN` | function | snake | 数学 | Semantic | — |
| `SQL_CONNECT` | function | snake | SQL | Semantic | — |
| `SQL_CONNECTION_OPEN` | function | snake | SQL | Semantic | — |
| `SQL_DISCONNECT` | function | snake | SQL | Semantic | — |
| `SQL_ESCAPE` | function | snake | SQL | Semantic | — |
| `SQL_EXECUTE_NONQUERY` | function | snake | SQL | Semantic | — |
| `SQL_EXECUTE_READER` | function | snake | SQL | Semantic | — |
| `SQL_EXECUTE_SCALAR_LONG` | function | snake | SQL | Semantic | — |
| `SQL_EXECUTE_SCALAR_STRING` | function | snake | SQL | Semantic | — |
| `SQL_EXPORT_DT_XML` | function | snake | SQL | Semantic | — |
| `SQL_EXPORT_MAP_XML` | function | snake | SQL | Semantic | — |
| `SQL_IMPORT_DT_XML` | function | snake | SQL | Semantic | — |
| `SQL_IMPORT_MAP_XML` | function | snake | SQL | Semantic | — |
| `SQL_IMPORT_XML_CUSTOM` | function | snake | SQL | Semantic | — |
| `SQL_P_EXECUTE_NONQUERY` | function | snake | SQL | Semantic | — |
| `SQL_P_EXECUTE_READER` | function | snake | SQL | Semantic | — |
| `SQL_P_EXECUTE_SCALAR_LONG` | function | snake | SQL | Semantic | — |
| `SQL_P_EXECUTE_SCALAR_STRING` | function | snake | SQL | Semantic | — |
| `SQL_READER_CLOSE` | function | snake | SQL | Semantic | — |
| `SQL_READER_GET_LONG` | function | snake | SQL | Semantic | — |
| `SQL_READER_GET_STRING` | function | snake | SQL | Semantic | — |
| `SQL_READER_ISNULL` | function | snake | SQL | Semantic | — |
| `SQL_READER_READ` | function | snake | SQL | Semantic | — |
| `TAN` | function | snake | 数学 | Semantic | — |
| `TEXT_BGC_OFF` | directive | snake | HTML 文本 | StateOnly | 仅清空 TextBackgroundColor，渲染线未消费 |
| `TEXT_BGC_ON` | directive | snake | HTML 文本 | StateOnly | 仅保存 TextBackgroundColor，渲染线未消费 |
| `TINPUTNF` | directive | snake | NF 输入 | Semantic | 无强制滚动步，与普通等待等价（已知差异） |
| `TINPUTSNF` | directive | snake | NF 输入 | Semantic | 无强制滚动步，与普通等待等价（已知差异） |
| `TONEINPUTNF` | directive | snake | NF 输入 | Semantic | 无强制滚动步，与普通等待等价（已知差异） |
| `TONEINPUTSNF` | directive | snake | NF 输入 | Semantic | 无强制滚动步，与普通等待等价（已知差异） |
| `TRYCALLSTR` | directive | snake | 动态调用 | Semantic | — |
| `TRYCCALLSTR` | directive | snake | 动态调用 | Semantic | — |
| `TRYCJUMPSTR` | directive | snake | 动态调用 | Semantic | — |
| `UNCHECKED_ADD` | function | snake | 数学 | Semantic | — |
| `UNCHECKED_MUL` | function | snake | 数学 | Semantic | — |
| `UNCHECKED_NEG` | function | snake | 数学 | Semantic | — |
| `UNCHECKED_SUB` | function | snake | 数学 | Semantic | — |

## baseline

| 键 | kind | 模块 | 处理体 |
|---|---|---|---|
| `ABS` | function | baseline | AbsMethod |
| `ADDCHARA` | directive | baseline | ADDCHARA_Instruction |
| `ADDCOPYCHARA` | directive | baseline | ADDCOPYCHARA_Instruction |
| `ADDDEFCHARA` | directive | baseline | FunctionCode.ADDDEFCHARA |
| `ADDSPCHARA` | directive | baseline | ADDCHARA_Instruction |
| `ADDVOIDCHARA` | directive | baseline | ADDVOIDCHARA_Instruction |
| `ALIGNMENT` | directive | baseline | FunctionCode.ALIGNMENT |
| `ALLSAMES` | function | baseline | AllsamesMethod |
| `ARRAYCOPY` | directive | baseline | FunctionCode.ARRAYCOPY |
| `ARRAYMSORT` | function | baseline | ArrayMultiSortMethod |
| `ARRAYMSORTEX` | function | baseline | ArrayMultiSortExMethod |
| `ARRAYREMOVE` | directive | baseline | FunctionCode.ARRAYREMOVE |
| `ARRAYSHIFT` | directive | baseline | FunctionCode.ARRAYSHIFT |
| `ARRAYSORT` | directive | baseline | FunctionCode.ARRAYSORT |
| `ASSERT` | directive | baseline | FunctionCode.ASSERT |
| `AWAIT` | directive | baseline | AWAIT_Instruction |
| `BAR` | directive | baseline | BAR_Instruction |
| `BARL` | directive | baseline | BAR_Instruction |
| `BARSTR` | function | baseline | BarStringMethod |
| `BEGIN` | directive | baseline | BEGIN_Instruction |
| `BINPUT` | directive | baseline | BINPUT_Instruction |
| `BINPUTS` | directive | baseline | BINPUTS_Instruction |
| `BITMAP_CACHE_ENABLE` | function | baseline | BitmapCacheEnableMethod |
| `BREAK` | directive | baseline | BREAK_Instruction |
| `BREAKBUTTON` | directive | baseline | BREAKBUTTON_Instruction |
| `CALL` | directive | baseline | CALL_Instruction |
| `CALLEVENT` | directive | baseline | CALLEVENT_Instruction |
| `CALLF` | directive | baseline | CALLF_Instruction |
| `CALLFORM` | directive | baseline | CALL_Instruction |
| `CALLFORMF` | directive | baseline | CALLF_Instruction |
| `CALLSHARP` | directive | baseline | CALLSHARP_Instruction |
| `CALLTRAIN` | directive | baseline | FunctionCode.CALLTRAIN |
| `CASE` | directive | baseline | ELSEIF_Instruction |
| `CASEELSE` | directive | baseline | ELSEIF_Instruction |
| `CATCH` | directive | baseline | CATCH_Instruction |
| `CBGCLEAR` | function | baseline | CBGClearMethod |
| `CBGCLEARBUTTON` | function | baseline | CBGClearButtonMethod |
| `CBGREMOVEBMAP` | function | baseline | CBGRemoveBMapMethod |
| `CBGREMOVERANGE` | function | baseline | CBGRemoveRangeMethod |
| `CBGSETBMAPG` | function | baseline | CBGSetBMapGMethod |
| `CBGSETBUTTONSPRITE` | function | baseline | CBGSETButtonSpriteMethod |
| `CBGSETG` | function | baseline | CBGSetGraphicsMethod |
| `CBGSETSPRITE` | function | baseline | CBGSetCIMGMethod |
| `CBRT` | function | baseline | CbrtMethod |
| `CHARATU` | function | baseline | CharAtMethod |
| `CHKCHARADATA` | function | baseline | CheckdataStrMethod |
| `CHKDATA` | function | baseline | CheckdataMethod |
| `CHKFONT` | function | baseline | CheckfontMethod |
| `CLEARBGIMAGE` | directive | baseline | CLEARBGIMAGE_Instruction |
| `CLEARBIT` | directive | baseline | SETBIT_Instruction |
| `CLEARLINE` | directive | baseline | CLEARLINE_Instruction |
| `CLEARMEMORY` | function | baseline | ClearMemoryMethod |
| `CLEARTEXTBOX` | directive | baseline | FunctionCode.CLEARTEXTBOX |
| `CLIENTHEIGHT` | function | baseline | ClientSizeMethod |
| `CLIENTWIDTH` | function | baseline | ClientSizeMethod |
| `CMATCH` | function | baseline | MatchMethod |
| `COLOR_FROMNAME` | function | baseline | ColorFromNameMethod |
| `COLOR_FROMRGB` | function | baseline | ColorFromRGBMethod |
| `CONTINUE` | directive | baseline | CONTINUE_Instruction |
| `CONVERT` | function | baseline | ConvertIntMethod |
| `COPYCHARA` | directive | baseline | COPYCHARA_Instruction |
| `CSVABL` | function | baseline | CsvDataMethod |
| `CSVBASE` | function | baseline | CsvDataMethod |
| `CSVCALLNAME` | function | baseline | CsvStrDataMethod |
| `CSVCFLAG` | function | baseline | CsvDataMethod |
| `CSVCSTR` | function | baseline | CsvcstrMethod |
| `CSVEQUIP` | function | baseline | CsvDataMethod |
| `CSVEXP` | function | baseline | CsvDataMethod |
| `CSVJUEL` | function | baseline | CsvDataMethod |
| `CSVMARK` | function | baseline | CsvDataMethod |
| `CSVMASTERNAME` | function | baseline | CsvStrDataMethod |
| `CSVNAME` | function | baseline | CsvStrDataMethod |
| `CSVNICKNAME` | function | baseline | CsvStrDataMethod |
| `CSVRELATION` | function | baseline | CsvDataMethod |
| `CSVTALENT` | function | baseline | CsvDataMethod |
| `CUPCHECK` | directive | baseline | FunctionCode.CUPCHECK |
| `CURRENTALIGN` | function | baseline | CurrentAlignMethod |
| `CURRENTREDRAW` | function | baseline | CurrentRedrawMethod |
| `CUSTOMDRAWLINE` | directive | baseline | CUSTOMDRAWLINE_Instruction |
| `CVARSET` | directive | baseline | CVARSET_Instruction |
| `DATA` | directive | baseline | FunctionCode.DATA |
| `DATAFORM` | directive | baseline | FunctionCode.DATAFORM |
| `DATALIST` | directive | baseline | FunctionCode.DATALIST |
| `DEBUGCLEAR` | directive | baseline | DEBUGCLEAR_Instruction |
| `DEBUGPRINT` | directive | baseline | DEBUGPRINT_Instruction |
| `DEBUGPRINTFORM` | directive | baseline | DEBUGPRINT_Instruction |
| `DEBUGPRINTFORML` | directive | baseline | DEBUGPRINT_Instruction |
| `DEBUGPRINTL` | directive | baseline | DEBUGPRINT_Instruction |
| `DELALLCHARA` | directive | baseline | FunctionCode.DELALLCHARA |
| `DELCHARA` | directive | baseline | ADDCHARA_Instruction |
| `DELDATA` | directive | baseline | DELDATA_Instruction |
| `DO` | directive | baseline | ENDIF_Instruction |
| `DOTRAIN` | directive | baseline | FunctionCode.DOTRAIN |
| `DRAWLINE` | directive | baseline | FunctionCode.DRAWLINE |
| `DRAWLINEFORM` | directive | baseline | FunctionCode.DRAWLINEFORM |
| `DT_CELL_GET` | function | baseline | DataTableCellGetMethod |
| `DT_CELL_GETS` | function | baseline | DataTableCellGetMethod |
| `DT_CELL_ISNULL` | function | baseline | DataTableCellGetMethod |
| `DT_CELL_SET` | function | baseline | DataTableCellSetMethod |
| `DT_CLEAR` | function | baseline | DataTableManagementMethod |
| `DT_COLUMN_ADD` | function | baseline | DataTableColumnManagementMethod |
| `DT_COLUMN_EXIST` | function | baseline | DataTableColumnManagementMethod |
| `DT_COLUMN_LENGTH` | function | baseline | DataTableLengthMethod |
| `DT_COLUMN_NAMES` | function | baseline | DataTableColumnManagementMethod |
| `DT_COLUMN_OPTIONS` | directive | baseline | DT_COLUMN_OPTIONS_Instruction |
| `DT_COLUMN_REMOVE` | function | baseline | DataTableColumnManagementMethod |
| `DT_CREATE` | function | baseline | DataTableManagementMethod |
| `DT_EXIST` | function | baseline | DataTableManagementMethod |
| `DT_FROMXML` | function | baseline | DataTableFromXmlMethod |
| `DT_NOCASE` | function | baseline | DataTableManagementMethod |
| `DT_RELEASE` | function | baseline | DataTableManagementMethod |
| `DT_ROW_ADD` | function | baseline | DataTableRowSetMethod |
| `DT_ROW_LENGTH` | function | baseline | DataTableLengthMethod |
| `DT_ROW_REMOVE` | function | baseline | DataTableRowRemoveMethod |
| `DT_ROW_SET` | function | baseline | DataTableRowSetMethod |
| `DT_SELECT` | function | baseline | DataTableSelectMethod |
| `DT_TOXML` | function | baseline | DataTableToXmlMethod |
| `DUMPRAND` | directive | baseline | DUMPRAND_Instruction |
| `ELSE` | directive | baseline | ELSEIF_Instruction |
| `ELSEIF` | directive | baseline | ELSEIF_Instruction |
| `ENCODETOUNI` | directive | baseline | FunctionCode.ENCODETOUNI |
| `ENCODETOUNI` | function | baseline | EncodeToUniMethod |
| `ENDCATCH` | directive | baseline | ENDIF_Instruction |
| `ENDDATA` | directive | baseline | DO_NOTHING_Instruction |
| `ENDFUNC` | directive | baseline | ENDIF_Instruction |
| `ENDIF` | directive | baseline | ENDIF_Instruction |
| `ENDLIST` | directive | baseline | FunctionCode.ENDLIST |
| `ENDNOSKIP` | directive | baseline | FunctionCode.ENDNOSKIP |
| `ENDSELECT` | directive | baseline | ENDIF_Instruction |
| `ENUMFILES` | function | baseline | EnumFilesMethod |
| `ENUMFUNCBEGINSWITH` | function | baseline | EnumNameMethod |
| `ENUMFUNCENDSWITH` | function | baseline | EnumNameMethod |
| `ENUMFUNCWITH` | function | baseline | EnumNameMethod |
| `ENUMMACROBEGINSWITH` | function | baseline | EnumNameMethod |
| `ENUMMACROENDSWITH` | function | baseline | EnumNameMethod |
| `ENUMMACROWITH` | function | baseline | EnumNameMethod |
| `ENUMVARBEGINSWITH` | function | baseline | EnumNameMethod |
| `ENUMVARENDSWITH` | function | baseline | EnumNameMethod |
| `ENUMVARWITH` | function | baseline | EnumNameMethod |
| `ERDNAME` | function | baseline | ErdNameMethod |
| `ESCAPE` | function | baseline | EscapeMethod |
| `EXISTCSV` | function | baseline | ExistCsvMethod |
| `EXISTFILE` | function | baseline | ExistFileMethod |
| `EXISTFUNCTION` | function | baseline | ExistFunctionMethod |
| `EXISTMETH` | function | baseline | ExistMethMethod |
| `EXISTSOUND` | function | baseline | ExistSoundMethod |
| `EXISTVAR` | function | baseline | ExistVarMethod |
| `EXPONENT` | function | baseline | ExpMethod |
| `FINDCHARA` | function | baseline | FindcharaMethod |
| `FINDELEMENT` | function | baseline | FindElementMethod |
| `FINDLASTCHARA` | function | baseline | FindcharaMethod |
| `FINDLASTELEMENT` | function | baseline | FindElementMethod |
| `FIND_CHARADATA` | function | baseline | FindFilesMethod |
| `FLOWINPUT` | function | baseline | FlowInputMethod |
| `FLOWINPUTS` | function | baseline | FlowInputsMethod |
| `FONTBOLD` | directive | baseline | FONTBOLD_Instruction |
| `FONTITALIC` | directive | baseline | FONTITALIC_Instruction |
| `FONTREGULAR` | directive | baseline | FONTREGULAR_Instruction |
| `FONTSTYLE` | directive | baseline | FunctionCode.FONTSTYLE |
| `FOR` | directive | baseline | REPEAT_Instruction |
| `FORCEKANA` | directive | baseline | FunctionCode.FORCEKANA |
| `FORCEWAIT` | directive | baseline | WAIT_Instruction |
| `FORCE_BEGIN` | directive | baseline | FORCE_BEGIN_Instruction |
| `FORCE_QUIT` | directive | baseline | FunctionCode.FORCE_QUIT |
| `FORCE_QUIT_AND_RESTART` | directive | baseline | FunctionCode.FORCE_QUIT_AND_RESTART |
| `FUNC` | directive | baseline | FunctionCode.FUNC |
| `GCLEAR` | function | baseline | GraphicsClearMethod |
| `GCREATE` | function | baseline | GraphicsCreateMethod |
| `GCREATED` | function | baseline | GraphicsStateMethod |
| `GCREATEFROMFILE` | function | baseline | GraphicsCreateFromFileMethod |
| `GDASHSTYLE` | function | baseline | GraphicsSetDashStyleMethod |
| `GDISPOSE` | function | baseline | GraphicsDisposeMethod |
| `GDRAWG` | function | baseline | GraphicsDrawGMethod |
| `GDRAWGWITHMASK` | function | baseline | GraphicsDrawGWithMaskMethod |
| `GDRAWGWITHROTATE` | function | baseline | GraphicsDrawGWithRotateMethod |
| `GDRAWLINE` | function | baseline | GraphicsDrawLineMethod |
| `GDRAWSPRITE` | function | baseline | GraphicsDrawSpriteMethod |
| `GDRAWTEXT` | function | baseline | GraphicsDrawStringMethod |
| `GETBGCOLOR` | function | baseline | GetBGColorMethod |
| `GETBIT` | function | baseline | GetbitMethod |
| `GETCHARA` | function | baseline | GetcharaMethod |
| `GETCOLOR` | function | baseline | GetColorMethod |
| `GETCONFIG` | function | baseline | GetConfigMethod |
| `GETCONFIGS` | function | baseline | GetConfigMethod |
| `GETDEFBGCOLOR` | function | baseline | GetBGColorMethod |
| `GETDEFCOLOR` | function | baseline | GetColorMethod |
| `GETDISPLAYLINE` | function | baseline | GetDisplayLineMethod |
| `GETDOINGFUNCTION` | function | baseline | GetDoingFunctionMethod |
| `GETEXPLV` | function | baseline | GetExpLVMethod |
| `GETFOCUSCOLOR` | function | baseline | GetFocusColorMethod |
| `GETFONT` | function | baseline | GetFontMethod |
| `GETKEY` | function | baseline | GetKeyStateMethod |
| `GETKEYTRIGGERED` | function | baseline | GetKeyStateMethod |
| `GETLINESTR` | function | baseline | GetLineStrMethod |
| `GETMEMORYUSAGE` | function | baseline | GetUsingMemoryMethod |
| `GETMETH` | function | baseline | GetMethMethod |
| `GETMETHS` | function | baseline | GetMethsMethod |
| `GETMILLISECOND` | function | baseline | GetmsMethod |
| `GETNUM` | function | baseline | GetnumMethod |
| `GETNUMB` | function | baseline | GetnumBMethod |
| `GETPALAMLV` | function | baseline | GetPalamLVMethod |
| `GETSECOND` | function | baseline | GetSecondMethod |
| `GETSPCHARA` | function | baseline | GetspcharaMethod |
| `GETSTYLE` | function | baseline | GetStyleMethod |
| `GETTEXTBOX` | function | baseline | GetTextBoxMethod |
| `GETTIME` | directive | baseline | FunctionCode.GETTIME |
| `GETTIME` | function | baseline | GettimeMethod |
| `GETTIMES` | function | baseline | GettimesMethod |
| `GETVAR` | function | baseline | GetVarMethod |
| `GETVARS` | function | baseline | GetVarsMethod |
| `GFILLRECTANGLE` | function | baseline | GraphicsFillRectangleMethod |
| `GGETBRUSH` | function | baseline | GraphicsStateMethod |
| `GGETCOLOR` | function | baseline | GraphicsGetColorMethod |
| `GGETFONT` | function | baseline | GraphicsStateStrMethod |
| `GGETFONTSIZE` | function | baseline | GraphicsStateMethod |
| `GGETFONTSTYLE` | function | baseline | GraphicsStateMethod |
| `GGETPEN` | function | baseline | GraphicsStateMethod |
| `GGETPENWIDTH` | function | baseline | GraphicsStateMethod |
| `GGETTEXTSIZE` | function | baseline | GraphicsGetTextSizeMethod |
| `GHEIGHT` | function | baseline | GraphicsStateMethod |
| `GLOAD` | function | baseline | GraphicsLoadMethod |
| `GOTO` | directive | baseline | GOTO_Instruction |
| `GOTOFORM` | directive | baseline | GOTO_Instruction |
| `GROUPMATCH` | function | baseline | GroupMatchMethod |
| `GSAVE` | function | baseline | GraphicsSaveMethod |
| `GSETBRUSH` | function | baseline | GraphicsSetBrushMethod |
| `GSETCOLOR` | function | baseline | GraphicsSetColorMethod |
| `GSETFONT` | function | baseline | GraphicsSetFontMethod |
| `GSETPEN` | function | baseline | GraphicsSetPenMethod |
| `GWIDTH` | function | baseline | GraphicsStateMethod |
| `HOTKEY_STATE` | function | baseline | HotkeyStateMethod |
| `HOTKEY_STATE_INIT` | function | baseline | HotkeyStateInitMethod |
| `HTML_ESCAPE` | function | baseline | HtmlEscapeMethod |
| `HTML_GETPRINTEDSTR` | function | baseline | HtmlGetPrintedStrMethod |
| `HTML_POPPRINTINGSTR` | function | baseline | HtmlPopPrintingStrMethod |
| `HTML_PRINT` | directive | baseline | HTML_PRINT_Instruction |
| `HTML_PRINT_ISLAND` | directive | baseline | HTML_PRINT_ISLAND_Instruction |
| `HTML_PRINT_ISLAND_CLEAR` | directive | baseline | HTML_PRINT_ISLAND_CLEAR_Instruction |
| `HTML_STRINGLEN` | function | baseline | HtmlStringLenMethod |
| `HTML_STRINGLINES` | function | baseline | HtmlStringLinesMethod |
| `HTML_SUBSTRING` | function | baseline | HtmlSubStringMethod |
| `HTML_TAGSPLIT` | directive | baseline | HTML_TAGSPLIT_Instruction |
| `HTML_TOPLAINTEXT` | function | baseline | HtmlToPlainTextMethod |
| `IF` | directive | baseline | IF_Instruction |
| `INITRAND` | directive | baseline | INITRAND_Instruction |
| `INPUT` | directive | baseline | INPUT_Instruction |
| `INPUTANY` | directive | baseline | INPUTANY_Instruction |
| `INPUTMOUSEKEY` | directive | baseline | INPUTMOUSEKEY_Instruction |
| `INPUTS` | directive | baseline | INPUTS_Instruction |
| `INRANGE` | function | baseline | InRangeMethod |
| `INRANGEARRAY` | function | baseline | InRangeArrayMethod |
| `INRANGECARRAY` | function | baseline | InRangeArrayMethod |
| `INVERTBIT` | directive | baseline | SETBIT_Instruction |
| `ISACTIVE` | function | baseline | IsActiveMethod |
| `ISDEFINED` | function | baseline | IsDefinedMethod |
| `ISNUMERIC` | function | baseline | IsNumericMethod |
| `ISSKIP` | function | baseline | IsSkipMethod |
| `JUMP` | directive | baseline | CALL_Instruction |
| `JUMPFORM` | directive | baseline | CALL_Instruction |
| `LIMIT` | function | baseline | GetLimitMethod |
| `LINEISEMPTY` | function | baseline | LineIsEmptyMethod |
| `LOADCHARA` | directive | baseline | LOADCHARA_Instruction |
| `LOADDATA` | directive | baseline | FunctionCode.LOADDATA |
| `LOADGAME` | directive | baseline | SAVELOADGAME_Instruction |
| `LOADGLOBAL` | directive | baseline | LOADGLOBAL_Instruction |
| `LOADTEXT` | function | baseline | LoadTextMethod |
| `LOADVAR` | directive | baseline | LOADVAR_Instruction |
| `LOG` | function | baseline | LogMethod |
| `LOG10` | function | baseline | LogMethod |
| `LOOP` | directive | baseline | LOOP_Instruction |
| `MAP_CLEAR` | function | baseline | MapDataOperationMethod |
| `MAP_CREATE` | function | baseline | MapManagementMethod |
| `MAP_EXIST` | function | baseline | MapManagementMethod |
| `MAP_FROMXML` | function | baseline | MapFromXmlMethod |
| `MAP_GET` | function | baseline | MapGetStrMethod |
| `MAP_GETKEYS` | function | baseline | MapGetStrMethod |
| `MAP_HAS` | function | baseline | MapDataOperationMethod |
| `MAP_RELEASE` | function | baseline | MapManagementMethod |
| `MAP_REMOVE` | function | baseline | MapDataOperationMethod |
| `MAP_SET` | function | baseline | MapDataOperationMethod |
| `MAP_SIZE` | function | baseline | MapDataOperationMethod |
| `MAP_TOXML` | function | baseline | MapGetStrMethod |
| `MATCH` | function | baseline | MatchMethod |
| `MAX` | function | baseline | MaxMethod |
| `MAXARRAY` | function | baseline | MaxArrayMethod |
| `MAXCARRAY` | function | baseline | MaxArrayMethod |
| `MESSKIP` | function | baseline | MesSkipMethod |
| `MIN` | function | baseline | MaxMethod |
| `MINARRAY` | function | baseline | MaxArrayMethod |
| `MINCARRAY` | function | baseline | MaxArrayMethod |
| `MONEYSTR` | function | baseline | MoneyStrMethod |
| `MOUSEB` | function | baseline | MouseButtonMethod |
| `MOUSESKIP` | function | baseline | MesSkipMethod |
| `MOUSEX` | function | baseline | MousePosMethod |
| `MOUSEY` | function | baseline | MousePosMethod |
| `MOVETEXTBOX` | function | baseline | MoveTextBoxMethod |
| `NEXT` | directive | baseline | REND_Instruction |
| `NOSAMES` | function | baseline | NosamesMethod |
| `NOSKIP` | directive | baseline | FunctionCode.NOSKIP |
| `ONEBINPUT` | directive | baseline | ONEBINPUT_Instruction |
| `ONEBINPUTS` | directive | baseline | ONEBINPUTS_Instruction |
| `ONEINPUT` | directive | baseline | ONEINPUT_Instruction |
| `ONEINPUTS` | directive | baseline | ONEINPUTS_Instruction |
| `OUTPUTLOG` | function | baseline | OutputlogMethod |
| `PICKUPCHARA` | directive | baseline | FunctionCode.PICKUPCHARA |
| `PLAYBGM` | directive | baseline | PLAYBGM_Instruction |
| `PLAYSOUND` | directive | baseline | PLAYSOUND_Instruction |
| `POWER` | directive | baseline | FunctionCode.POWER |
| `POWER` | function | baseline | PowerMethod |
| `PRINT` | directive | baseline | PRINT_Instruction |
| `PRINTBUTTON` | directive | baseline | FunctionCode.PRINTBUTTON |
| `PRINTBUTTONC` | directive | baseline | FunctionCode.PRINTBUTTONC |
| `PRINTBUTTONLC` | directive | baseline | FunctionCode.PRINTBUTTONLC |
| `PRINTC` | directive | baseline | PRINT_Instruction |
| `PRINTCD` | directive | baseline | PRINT_Instruction |
| `PRINTCK` | directive | baseline | PRINT_Instruction |
| `PRINTCLENGTH` | function | baseline | PrintCLengthMethod |
| `PRINTCPERLINE` | directive | baseline | FunctionCode.PRINTCPERLINE |
| `PRINTCPERLINE` | function | baseline | GetPrintCPerLineMethod |
| `PRINTD` | directive | baseline | PRINT_Instruction |
| `PRINTDATA` | directive | baseline | PRINT_DATA_Instruction |
| `PRINTDATAD` | directive | baseline | PRINT_DATA_Instruction |
| `PRINTDATADL` | directive | baseline | PRINT_DATA_Instruction |
| `PRINTDATADW` | directive | baseline | PRINT_DATA_Instruction |
| `PRINTDATAK` | directive | baseline | PRINT_DATA_Instruction |
| `PRINTDATAKL` | directive | baseline | PRINT_DATA_Instruction |
| `PRINTDATAKW` | directive | baseline | PRINT_DATA_Instruction |
| `PRINTDATAL` | directive | baseline | PRINT_DATA_Instruction |
| `PRINTDATAW` | directive | baseline | PRINT_DATA_Instruction |
| `PRINTDL` | directive | baseline | PRINT_Instruction |
| `PRINTDW` | directive | baseline | PRINT_Instruction |
| `PRINTFORM` | directive | baseline | PRINT_Instruction |
| `PRINTFORMC` | directive | baseline | PRINT_Instruction |
| `PRINTFORMCD` | directive | baseline | PRINT_Instruction |
| `PRINTFORMCK` | directive | baseline | PRINT_Instruction |
| `PRINTFORMD` | directive | baseline | PRINT_Instruction |
| `PRINTFORMDL` | directive | baseline | PRINT_Instruction |
| `PRINTFORMDW` | directive | baseline | PRINT_Instruction |
| `PRINTFORMK` | directive | baseline | PRINT_Instruction |
| `PRINTFORMKL` | directive | baseline | PRINT_Instruction |
| `PRINTFORMKW` | directive | baseline | PRINT_Instruction |
| `PRINTFORML` | directive | baseline | PRINT_Instruction |
| `PRINTFORMLC` | directive | baseline | PRINT_Instruction |
| `PRINTFORMLCD` | directive | baseline | PRINT_Instruction |
| `PRINTFORMLCK` | directive | baseline | PRINT_Instruction |
| `PRINTFORMN` | directive | baseline | PRINT_Instruction |
| `PRINTFORMS` | directive | baseline | PRINT_Instruction |
| `PRINTFORMSD` | directive | baseline | PRINT_Instruction |
| `PRINTFORMSDL` | directive | baseline | PRINT_Instruction |
| `PRINTFORMSDW` | directive | baseline | PRINT_Instruction |
| `PRINTFORMSK` | directive | baseline | PRINT_Instruction |
| `PRINTFORMSKL` | directive | baseline | PRINT_Instruction |
| `PRINTFORMSKW` | directive | baseline | PRINT_Instruction |
| `PRINTFORMSL` | directive | baseline | PRINT_Instruction |
| `PRINTFORMSN` | directive | baseline | PRINT_Instruction |
| `PRINTFORMSW` | directive | baseline | PRINT_Instruction |
| `PRINTFORMW` | directive | baseline | PRINT_Instruction |
| `PRINTK` | directive | baseline | PRINT_Instruction |
| `PRINTKL` | directive | baseline | PRINT_Instruction |
| `PRINTKW` | directive | baseline | PRINT_Instruction |
| `PRINTL` | directive | baseline | PRINT_Instruction |
| `PRINTLC` | directive | baseline | PRINT_Instruction |
| `PRINTLCD` | directive | baseline | PRINT_Instruction |
| `PRINTLCK` | directive | baseline | PRINT_Instruction |
| `PRINTN` | directive | baseline | PRINT_Instruction |
| `PRINTPLAIN` | directive | baseline | FunctionCode.PRINTPLAIN |
| `PRINTPLAINFORM` | directive | baseline | FunctionCode.PRINTPLAINFORM |
| `PRINTS` | directive | baseline | PRINT_Instruction |
| `PRINTSD` | directive | baseline | PRINT_Instruction |
| `PRINTSDL` | directive | baseline | PRINT_Instruction |
| `PRINTSDW` | directive | baseline | PRINT_Instruction |
| `PRINTSINGLE` | directive | baseline | PRINT_Instruction |
| `PRINTSINGLED` | directive | baseline | PRINT_Instruction |
| `PRINTSINGLEFORM` | directive | baseline | PRINT_Instruction |
| `PRINTSINGLEFORMD` | directive | baseline | PRINT_Instruction |
| `PRINTSINGLEFORMK` | directive | baseline | PRINT_Instruction |
| `PRINTSINGLEFORMS` | directive | baseline | PRINT_Instruction |
| `PRINTSINGLEFORMSD` | directive | baseline | PRINT_Instruction |
| `PRINTSINGLEFORMSK` | directive | baseline | PRINT_Instruction |
| `PRINTSINGLEK` | directive | baseline | PRINT_Instruction |
| `PRINTSINGLES` | directive | baseline | PRINT_Instruction |
| `PRINTSINGLESD` | directive | baseline | PRINT_Instruction |
| `PRINTSINGLESK` | directive | baseline | PRINT_Instruction |
| `PRINTSINGLEV` | directive | baseline | PRINT_Instruction |
| `PRINTSINGLEVD` | directive | baseline | PRINT_Instruction |
| `PRINTSINGLEVK` | directive | baseline | PRINT_Instruction |
| `PRINTSK` | directive | baseline | PRINT_Instruction |
| `PRINTSKL` | directive | baseline | PRINT_Instruction |
| `PRINTSKW` | directive | baseline | PRINT_Instruction |
| `PRINTSL` | directive | baseline | PRINT_Instruction |
| `PRINTSN` | directive | baseline | PRINT_Instruction |
| `PRINTSW` | directive | baseline | PRINT_Instruction |
| `PRINTV` | directive | baseline | PRINT_Instruction |
| `PRINTVD` | directive | baseline | PRINT_Instruction |
| `PRINTVDL` | directive | baseline | PRINT_Instruction |
| `PRINTVDW` | directive | baseline | PRINT_Instruction |
| `PRINTVK` | directive | baseline | PRINT_Instruction |
| `PRINTVKL` | directive | baseline | PRINT_Instruction |
| `PRINTVKW` | directive | baseline | PRINT_Instruction |
| `PRINTVL` | directive | baseline | PRINT_Instruction |
| `PRINTVN` | directive | baseline | PRINT_Instruction |
| `PRINTVW` | directive | baseline | PRINT_Instruction |
| `PRINTW` | directive | baseline | PRINT_Instruction |
| `PRINT_ABL` | directive | baseline | FunctionCode.PRINT_ABL |
| `PRINT_EXP` | directive | baseline | FunctionCode.PRINT_EXP |
| `PRINT_IMG` | directive | baseline | PRINT_IMG_Instruction |
| `PRINT_ITEM` | directive | baseline | FunctionCode.PRINT_ITEM |
| `PRINT_MARK` | directive | baseline | FunctionCode.PRINT_MARK |
| `PRINT_PALAM` | directive | baseline | FunctionCode.PRINT_PALAM |
| `PRINT_RECT` | directive | baseline | PRINT_RECT_Instruction |
| `PRINT_SHOPITEM` | directive | baseline | FunctionCode.PRINT_SHOPITEM |
| `PRINT_SPACE` | directive | baseline | PRINT_SPACE_Instruction |
| `PRINT_TALENT` | directive | baseline | FunctionCode.PRINT_TALENT |
| `PUTFORM` | directive | baseline | FunctionCode.PUTFORM |
| `QUIT` | directive | baseline | FunctionCode.QUIT |
| `QUIT_AND_RESTART` | directive | baseline | FunctionCode.QUIT_AND_RESTART |
| `RAND` | function | baseline | RandMethod |
| `RANDOMIZE` | directive | baseline | RANDOMIZE_Instruction |
| `REDRAW` | directive | baseline | FunctionCode.REDRAW |
| `REF` | directive | baseline | REF_Instruction |
| `REFBYNAME` | directive | baseline | REF_Instruction |
| `REGEXPMATCH` | function | baseline | RegexpMatchMethod |
| `REMOVEBGIMAGE` | directive | baseline | REMOVEBGIMAGE_Instruction |
| `REND` | directive | baseline | REND_Instruction |
| `REPEAT` | directive | baseline | REPEAT_Instruction |
| `REPLACE` | function | baseline | ReplaceMethod |
| `RESETBGCOLOR` | directive | baseline | RESETBGCOLOR_Instruction |
| `RESETCOLOR` | directive | baseline | RESETCOLOR_Instruction |
| `RESETDATA` | directive | baseline | RESETDATA_Instruction |
| `RESETGLOBAL` | directive | baseline | RESETGLOBAL_Instruction |
| `RESET_STAIN` | directive | baseline | FunctionCode.RESET_STAIN |
| `RESTART` | directive | baseline | RESTART_Instruction |
| `RESUMETEXTBOX` | function | baseline | MoveTextBoxMethod |
| `RETURN` | directive | baseline | RETURN_Instruction |
| `RETURNF` | directive | baseline | RETURNF_Instruction |
| `RETURNFORM` | directive | baseline | RETURNFORM_Instruction |
| `REUSELASTLINE` | directive | baseline | REUSELASTLINE_Instruction |
| `SAVECHARA` | directive | baseline | SAVECHARA_Instruction |
| `SAVEDATA` | directive | baseline | FunctionCode.SAVEDATA |
| `SAVEGAME` | directive | baseline | SAVELOADGAME_Instruction |
| `SAVEGLOBAL` | directive | baseline | SAVEGLOBAL_Instruction |
| `SAVENOS` | directive | baseline | FunctionCode.SAVENOS |
| `SAVENOS` | function | baseline | GetSaveNosMethod |
| `SAVETEXT` | function | baseline | SaveTextMethod |
| `SAVEVAR` | directive | baseline | SAVEVAR_Instruction |
| `SELECTCASE` | directive | baseline | SELECTCASE_Instruction |
| `SETANIMETIMER` | function | baseline | SetAnimeTimerMethod |
| `SETBGCOLOR` | directive | baseline | FunctionCode.SETBGCOLOR |
| `SETBGCOLORBYNAME` | directive | baseline | FunctionCode.SETBGCOLORBYNAME |
| `SETBGIMAGE` | directive | baseline | SETBGIMAGE_Instruction |
| `SETBGMVOLUME` | directive | baseline | SETBGMVOLUME_Instruction |
| `SETBIT` | directive | baseline | SETBIT_Instruction |
| `SETCOLOR` | directive | baseline | FunctionCode.SETCOLOR |
| `SETCOLORBYNAME` | directive | baseline | FunctionCode.SETCOLORBYNAME |
| `SETFONT` | directive | baseline | FunctionCode.SETFONT |
| `SETSOUNDVOLUME` | directive | baseline | SETSOUNDVOLUME_Instruction |
| `SETTEXTBOX` | function | baseline | ChangeTextBoxMethod |
| `SETVAR` | function | baseline | SetVarMethod |
| `SIF` | directive | baseline | SIF_Instruction |
| `SIGN` | function | baseline | SignMethod |
| `SKIPDISP` | directive | baseline | FunctionCode.SKIPDISP |
| `SKIPLOG` | directive | baseline | FunctionCode.SKIPLOG |
| `SORTCHARA` | directive | baseline | SORTCHARA_Instruction |
| `SPLIT` | directive | baseline | FunctionCode.SPLIT |
| `SPRITEANIMEADDFRAME` | function | baseline | SpriteAnimeAddFrameMethod |
| `SPRITEANIMECREATE` | function | baseline | SpriteAnimeCreateMethod |
| `SPRITECREATE` | function | baseline | SpriteCreateMethod |
| `SPRITECREATED` | function | baseline | SpriteStateMethod |
| `SPRITEDISPOSE` | function | baseline | SpriteDisposeMethod |
| `SPRITEDISPOSEALL` | function | baseline | SpriteDisposeAllMethod |
| `SPRITEGETCOLOR` | function | baseline | SpriteGetColorMethod |
| `SPRITEHEIGHT` | function | baseline | SpriteStateMethod |
| `SPRITEMOVE` | function | baseline | SpriteSetPosMethod |
| `SPRITEPOSX` | function | baseline | SpriteStateMethod |
| `SPRITEPOSY` | function | baseline | SpriteStateMethod |
| `SPRITESETPOS` | function | baseline | SpriteSetPosMethod |
| `SPRITEWIDTH` | function | baseline | SpriteStateMethod |
| `SQRT` | function | baseline | SqrtMethod |
| `STOPBGM` | directive | baseline | STOPBGM_Instruction |
| `STOPCALLTRAIN` | directive | baseline | FunctionCode.STOPCALLTRAIN |
| `STOPSOUND` | directive | baseline | STOPSOUND_Instruction |
| `STRCOUNT` | function | baseline | StrCountMethod |
| `STRDATA` | directive | baseline | FunctionCode.STRDATA |
| `STRFIND` | function | baseline | StrfindMethod |
| `STRFINDU` | function | baseline | StrfindMethod |
| `STRFORM` | function | baseline | StrFormMethod |
| `STRJOIN` | function | baseline | JoinMethod |
| `STRLEN` | directive | baseline | STRLEN_Instruction |
| `STRLENFORM` | directive | baseline | STRLEN_Instruction |
| `STRLENFORMU` | directive | baseline | STRLEN_Instruction |
| `STRLENS` | function | baseline | StrlenMethod |
| `STRLENSU` | function | baseline | StrlenuMethod |
| `STRLENU` | directive | baseline | STRLEN_Instruction |
| `SUBSTRING` | function | baseline | SubstringMethod |
| `SUBSTRINGU` | function | baseline | SubstringuMethod |
| `SUMARRAY` | function | baseline | SumArrayMethod |
| `SUMCARRAY` | function | baseline | SumArrayMethod |
| `SWAP` | directive | baseline | FunctionCode.SWAP |
| `SWAPCHARA` | directive | baseline | SWAPCHARA_Instruction |
| `THROW` | directive | baseline | FunctionCode.THROW |
| `TIMES` | directive | baseline | TIMES_Instruction |
| `TINPUT` | directive | baseline | TINPUT_Instruction |
| `TINPUTS` | directive | baseline | TINPUTS_Instruction |
| `TOFULL` | function | baseline | StrChangeStyleMethod |
| `TOHALF` | function | baseline | StrChangeStyleMethod |
| `TOINT` | function | baseline | ToIntMethod |
| `TOLOWER` | function | baseline | StrChangeStyleMethod |
| `TONEINPUT` | directive | baseline | TINPUT_Instruction |
| `TONEINPUTS` | directive | baseline | TINPUTS_Instruction |
| `TOOLTIP_CUSTOM` | directive | baseline | TOOLTIP_CUSTOM_Instruction |
| `TOOLTIP_FORMAT` | directive | baseline | TOOLTIP_FORMAT_Instruction |
| `TOOLTIP_IMG` | directive | baseline | TOOLTIP_IMG_Instruction |
| `TOOLTIP_SETCOLOR` | directive | baseline | TOOLTIP_SETCOLOR_Instruction |
| `TOOLTIP_SETDELAY` | directive | baseline | TOOLTIP_SETDELAY_Instruction |
| `TOOLTIP_SETDURATION` | directive | baseline | TOOLTIP_SETDURATION_Instruction |
| `TOOLTIP_SETFONT` | directive | baseline | TOOLTIP_SETFONT_Instruction |
| `TOOLTIP_SETFONTSIZE` | directive | baseline | TOOLTIP_SETFONTSIZE_Instruction |
| `TOSTR` | function | baseline | ToStrMethod |
| `TOUPPER` | function | baseline | StrChangeStyleMethod |
| `TRYCALL` | directive | baseline | CALL_Instruction |
| `TRYCALLF` | directive | baseline | TRYCALLF_Instruction |
| `TRYCALLFORM` | directive | baseline | CALL_Instruction |
| `TRYCALLFORMF` | directive | baseline | TRYCALLF_Instruction |
| `TRYCALLLIST` | directive | baseline | FunctionCode.TRYCALLLIST |
| `TRYCCALL` | directive | baseline | CALL_Instruction |
| `TRYCCALLFORM` | directive | baseline | CALL_Instruction |
| `TRYCGOTO` | directive | baseline | GOTO_Instruction |
| `TRYCGOTOFORM` | directive | baseline | GOTO_Instruction |
| `TRYCJUMP` | directive | baseline | CALL_Instruction |
| `TRYCJUMPFORM` | directive | baseline | CALL_Instruction |
| `TRYGOTO` | directive | baseline | GOTO_Instruction |
| `TRYGOTOFORM` | directive | baseline | GOTO_Instruction |
| `TRYGOTOLIST` | directive | baseline | FunctionCode.TRYGOTOLIST |
| `TRYJUMP` | directive | baseline | CALL_Instruction |
| `TRYJUMPFORM` | directive | baseline | CALL_Instruction |
| `TRYJUMPLIST` | directive | baseline | FunctionCode.TRYJUMPLIST |
| `TWAIT` | directive | baseline | TWAIT_Instruction |
| `UNICODE` | function | baseline | UnicodeMethod |
| `UNICODEBYTE` | function | baseline | UnicodeByteMethod |
| `UPCHECK` | directive | baseline | FunctionCode.UPCHECK |
| `UPDATECHECK` | directive | baseline | UPDATECHECK_Instruction |
| `VARSET` | directive | baseline | VARSET_Instruction |
| `VARSETEX` | function | baseline | VarSetExMethod |
| `VARSIZE` | directive | baseline | FunctionCode.VARSIZE |
| `VARSIZE` | function | baseline | VarsizeMethod |
| `WAIT` | directive | baseline | WAIT_Instruction |
| `WAITANYKEY` | directive | baseline | WAITANYKEY_Instruction |
| `WEND` | directive | baseline | WEND_Instruction |
| `WHILE` | directive | baseline | WHILE_Instruction |
| `XML_ADDATTRIBUTE` | function | baseline | XmlAddNodeMethod |
| `XML_ADDATTRIBUTE_BYNAME` | function | baseline | XmlAddNodeMethod |
| `XML_ADDNODE` | function | baseline | XmlAddNodeMethod |
| `XML_ADDNODE_BYNAME` | function | baseline | XmlAddNodeMethod |
| `XML_DOCUMENT` | function | baseline | XmlDocumentMethod |
| `XML_EXIST` | function | baseline | XmlDocumentMethod |
| `XML_GET` | function | baseline | XmlGetMethod |
| `XML_GET_BYNAME` | function | baseline | XmlGetMethod |
| `XML_RELEASE` | function | baseline | XmlDocumentMethod |
| `XML_REMOVEATTRIBUTE` | function | baseline | XmlRemoveNodeMethod |
| `XML_REMOVEATTRIBUTE_BYNAME` | function | baseline | XmlRemoveNodeMethod |
| `XML_REMOVENODE` | function | baseline | XmlRemoveNodeMethod |
| `XML_REMOVENODE_BYNAME` | function | baseline | XmlRemoveNodeMethod |
| `XML_REPLACE` | function | baseline | XmlReplaceMethod |
| `XML_REPLACE_BYNAME` | function | baseline | XmlReplaceMethod |
| `XML_SET` | function | baseline | XmlSetMethod |
| `XML_SET_BYNAME` | function | baseline | XmlSetMethod |
| `XML_TOSTR` | function | baseline | XmlToStrMethod |
