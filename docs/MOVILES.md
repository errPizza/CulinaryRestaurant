# Android e iOS — Culinary Restaurant 0.1.1

Interfaz compartida Avalonia; el móvil es cliente de una sucursal, no almacén ni servidor. Necesita identidad, CA pública verificada y alta de dispositivo. No se incrustan claves privadas, contraseñas ni certificados maestros. No hay bypass TLS.

## Android

Proyecto `src/MesaNexo.Android`, net10.0-android, mínimo Android 12/API 31, identificador `mx.mesanexo.nativeclient`. Compilado con workload .NET Android, JDK 21 y SDK instalado:

```powershell
dotnet build src/MesaNexo.Android -c Debug -p:AndroidSdkDirectory="C:\Program Files (x86)\Android\android-sdk" -p:JavaSdkDirectory="C:\Program Files\Android\openjdk\jdk-21.0.8"
```

El APK entregado es de depuración y tiene firma debug. Para publicación configurar keystore propio fuera del repositorio y propiedades de firma del workload; no distribuir esa clave. La aplicación deshabilita backup Android de credenciales y tráfico cleartext. Keystore protege el cifrado local. NSD descubre candidatos, no confianza. Hay avisos de obsolescencia de ResolveService/Host en API 34+, sin errores de compilación.

Pendiente: instalar en dispositivo/emulador, verificar resolución, TLS contra certificado provisionado, suspensión/reanudación, rotación, red perdida y cola tras cierre del proceso. Probar también dirección manual; el descubrimiento multicast puede estar restringido por la red.

## iOS

Proyecto `src/MesaNexo.iOS`, net10.0-ios, mínimo iOS 18. Fuente con AvaloniaAppDelegate, Keychain y Bonjour NSNetServiceBrowser. Info.plist declara red local y tipo Bonjour. No se compiló ni ejecutó y no se entrega IPA.

Se requiere Mac con versión de macOS/Xcode compatible con el workload .NET 10 iOS instalado, aceptación de licencias de Xcode, SDK iOS, equipo Apple Developer, identificador y perfiles de firma para dispositivo. Verificar versiones del workload/Xcode en documentación oficial antes de instalar. En Mac:

```sh
dotnet workload install ios
dotnet restore src/MesaNexo.iOS
dotnet build src/MesaNexo.iOS -c Debug -p:RuntimeIdentifier=iossimulator-arm64
# Para dispositivo: ios-arm64, CodesignKey y CodesignProvision propios.
```

El adaptador Bonjour se escribió contra referencias iOS locales, lo cual no demuestra compilación. Validar permiso de red local, resolución Bonjour, Keychain, TLS y ciclo de vida en dispositivo real. Certificados/perfiles, TestFlight y App Store son trabajo pendiente.

Fuentes oficiales: [Android Avalonia](https://docs.avaloniaui.net/docs/platform-specific-guides/android), [iOS Avalonia](https://docs.avaloniaui.net/docs/platform-specific-guides/ios).

## Ampliación DEVMap — 0.2.0

Versión visible 0.2.0, versionCode 3, mismos identificadores para compatibilidad. SignalR usa TLS con CA/nombre provisionados, credenciales protegidas y reconexión por snapshot; la revisión efímera no es el estado de cocina. Catálogo protegido y borradores/peticiones conservan UUID ante respuesta perdida. El perfil dueño requiere tenant/branch y alta de dispositivo de la central; los reportes muestran origen y retraso.

FCM/APNs están en biblioteca de servidor aislada; no hay activación, registro real ni envío. No se promete proceso móvil continuo en segundo plano. La compilación Android y la inspección del APK no demuestran experiencia en dispositivo. iOS sigue sin compilar por falta de Mac/Xcode/firma; no se entrega IPA.
