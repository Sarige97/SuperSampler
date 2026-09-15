// .NET Framework 4.6 的 BCL 里没有 IsExternalInit，编译器需要它来支持 init 访问器与 record。
// 内部 shim：仅本程序集编译期可见，不对外发布。使用 record/init 的程序集各自带一份。
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit { }
