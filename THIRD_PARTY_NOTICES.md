# Éléments tiers

Le code de ce dépôt est publié sous licence MIT (fichier `LICENSE`). Les fichiers qui en sont tirés, `Arr411.exe` et le zip qui le contient, embarquent aussi les éléments ci-dessous, qui gardent leur propre licence.

L'application web, le moteur et les scripts de page embarqués dans `Arr411.exe` viennent tels quels de [arr411-android](https://github.com/HugoMeyer/arr411-android) et de [arr411-chrome](https://github.com/HugoMeyer/arr411-chrome), deux projets MIT du même auteur. `arr411-android` a ses propres [éléments tiers](https://github.com/HugoMeyer/arr411-android/blob/main/THIRD_PARTY_NOTICES.md) ; le seul élément tiers de ce qui est embarqué est celui de la section suivante.

## Heroicons

Les icônes de l'application web (`app/icons.js` dans `arr411-android`) viennent de [Heroicons](https://heroicons.com) v2 (série « outline »).

```
MIT License

Copyright (c) Tailwind Labs, Inc.

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

## Microsoft.Web.WebView2

`Arr411.exe` inclut le wrapper managé et le chargeur du [SDK WebView2](https://aka.ms/webview) (paquet NuGet `Microsoft.Web.WebView2`). Le paquet fournit sa propre liste d'éléments tiers ([page NuGet](https://www.nuget.org/packages/Microsoft.Web.WebView2)).

Le runtime WebView2 (Microsoft Edge) n'est pas dans le zip : il est installé sur le PC de l'utilisateur, sous les conditions de licence de Microsoft.

```
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## .NET

`Arr411.exe` est autonome : il contient le runtime .NET et ses bibliothèques, dont `System.Security.Cryptography.ProtectedData`. Ils sont publiés sous licence MIT, Copyright (c) .NET Foundation and Contributors, Microsoft Corporation. Les éléments tiers du runtime sont publiés dans [dotnet/runtime](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT).

## Développement uniquement

Les tests utilisent [xUnit](https://xunit.net) (Apache-2.0) et `Microsoft.NET.Test.Sdk` (MIT). Aucun n'est inclus dans `Arr411.exe`.
