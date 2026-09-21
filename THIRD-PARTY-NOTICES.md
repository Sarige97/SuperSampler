# Third-Party Notices（第三方组件声明）

本仓库的**合并版发布程序集 `SuperSampler.Core.dll`**（由 `dotnet build -c Release -p:EnableILRepack=true`
产出）通过 ILRepack 将以下第三方组件内嵌其中。按其开源许可证要求，特此声明其来源与许可：

---

## Jint

- **用途**：点位脚本解码引擎（`<Script>`，Jint ES5.1），内嵌进合并版 `SuperSampler.Core.dll`。
- **版本**：2.11.58
- **作者 / 版权**：Sebastien Ros（© 2013）
- **项目主页**：https://github.com/sebastienros/jint
- **许可证**：MIT License

```text
MIT License

Copyright (c) 2013 Sebastien Ros

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

> 本声明覆盖**合并发布版**（ILRepack 四合一，Jint 内嵌）。日常构建的 `SuperSampler.Core.dll` / `Jint.dll`
> 各程序集仍以独立文件分发，其各自许可证见对应项目。
> SuperSampler 本身的许可证见根目录 [`LICENSE`](LICENSE)。
