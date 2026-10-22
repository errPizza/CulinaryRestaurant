# Desarrollo en VS Code

Abrir `CulinaryRestaurant.code-workspace` desde esta carpeta nativa. Instalar SDK .NET 10 compatible con global.json, extensión C# de Microsoft y Python con PyNaCl para el laboratorio. No abrir SoftwarePrototipo para editar la aplicación nativa.

Tareas en `.vscode/tasks.json`: compilar, regresión y laboratorio; depuración de escritorio en `.vscode/launch.json`. También pueden ejecutarse `dotnet build MesaNexo.slnx` y `tools/Test.ps1`. El laboratorio interactivo `tools/Start-Lab.ps1` pide credenciales propias y usa .lab/manual, sin cuentas demo ni datos productivos.

Las variables DOTNET_CLI_HOME y NUGET_PACKAGES de las tareas apuntan a caches locales excluidas del paquete fuente. Para Android instalar el workload y configurar SDK/JDK según MOVILES.md. iOS requiere Mac/Xcode y firma propia. No guardar contraseñas, keyrings, PFX, service accounts ni keystores en tareas/configuración pública.

Paquetes: `tools/build.ps1 -NsisCompiler RUTA_MAKENSIS`, compilación Android según MOVILES.md, después `tools/package_deb.py`, `tools/Verify-Branding.ps1` y `tools/package_delivery.py` una vez generada evidencia de arranque/firma. Consultar el script para opciones concretas. Esto construye artefactos; no instala, publica ni activa integraciones.
