using System;
using System.Runtime.InteropServices;

namespace KTY_MARKER;

/// <summary>
/// [DllExport]属性のスタブ。Visual Studio無しで`dotnet build`(クロスプラットフォームの
/// Coreランタイム版MSBuild)による簡易コンパイルチェックを行うときだけ使う -
/// KTY_MARKER.csprojのこのファイルのCompile項目にある`MSBuildRuntimeType`条件を参照。
/// 実際のビルド(Visual Studio / そのMSBuild.exe)では、DllExportのNuGetパッケージが
/// 本物の`DllExportAttribute`を同じ名前空間に生成するため、このスタブはコンパイル対象から
/// 除外され、両者が衝突することはない。
/// </summary>
internal sealed class DllExportAttribute : Attribute
{
    public DllExportAttribute() { }
    public DllExportAttribute(string exportName) { }
    public DllExportAttribute(CallingConvention convention) { }
    public DllExportAttribute(string exportName, CallingConvention convention) { }

    public CallingConvention CallingConvention { get; set; }
    public string ExportName { get; set; }
}
