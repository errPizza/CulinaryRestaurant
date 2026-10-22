# Versiones verificadas — 15 de octubre de 2026
.NET 10 LTS; SDK disponible 10.0.301 (runtime 10.0.9). Los paquetes autocontenidos se publican con runtime 10.0.13, comprobado en el registro oficial de Microsoft/NuGet. El SDK instalado conserva runtime 10.0.9 para ejecutar las pruebas de desarrollo.
Avalonia 12.1.4 (versión estable comprobada en el registro oficial NuGet). Escritorio net10.0, Android net10.0-android, iOS net10.0-ios. No WebView ni navegador.
Fuentes oficiales consultadas:
- https://dotnet.microsoft.com/en-us/platform/support/policy (.NET 10 LTS hasta noviembre de 2028).
- https://docs.avaloniaui.net/docs/supported-platforms (móvil requiere .NET 10; Debian 13 soportado; Windows 10 22H2 nivel 2).
- https://api.nuget.org/v3-flatcontainer/avalonia/index.json
Python de laboratorio: 3.13.9, PyNaCl 1.6.2, SQLite 3.50.4; el despliegue utiliza el Python/PyNaCl de CulinaryOS sin sustituirlo.
La compatibilidad declarada por el framework no demuestra ejecución de esta aplicación en cada sistema.