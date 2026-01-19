# PS5 Upload Suite Mobile - Build Instructions

## Προαπαιτούμενα

1. **Android Studio** (εγκατεστημένο ✓)
2. **Android SDK API Level 31**

## Βήμα 1: Εγκατάσταση Android SDK API 31

1. Άνοιξε το **Android Studio**
2. Πήγαινε στο **Tools → SDK Manager**
3. Στην καρτέλα **SDK Platforms**:
   - Τσέκαρε το **Android 12.0 (S) - API Level 31**
   - Κάνε click **Apply** και περίμενε να ολοκληρωθεί η εγκατάσταση

## Βήμα 2: Build το Android APK

Μετά την εγκατάσταση του API 31, τρέξε:

```powershell
cd "C:\Users\HACKMAN\Desktop\ps5 test\my_projects\ps5_upload_suite\mobile"
dotnet build -f net6.0-android -p:AndroidSdkDirectory="C:\Users\HACKMAN\AppData\Local\Android\Sdk"
```

## Βήμα 3: Δημιουργία APK για εγκατάσταση

Για να δημιουργήσεις το APK αρχείο:

```powershell
dotnet publish -f net6.0-android -c Release -p:AndroidSdkDirectory="C:\Users\HACKMAN\AppData\Local\Android\Sdk"
```

Το APK θα βρίσκεται στο:
```
bin\Release\net6.0-android\publish\com.ps5tools.uploadsuite-Signed.apk
```

## Βήμα 4: Εγκατάσταση στο Android

1. Μεταφορά του APK στο κινητό σου
2. Ενεργοποίηση **"Install from Unknown Sources"** στις ρυθμίσεις
3. Άνοιγμα του APK και εγκατάσταση

## Features του App

- 📱 Connect στο PS5 μέσω FTP (port 2121)
- 📁 Browse files και folders
- ⬆️ Upload files από το κινητό στο PS5
- ⬇️ Download files από το PS5 στο κινητό
- 🗑️ Delete files
- 📊 Progress bar για transfers
- 🎨 Modern dark theme UI

## Troubleshooting

### "Android SDK not found"
Βεβαιώσου ότι το Android Studio είναι εγκατεστημένο και το SDK path είναι:
`C:\Users\HACKMAN\AppData\Local\Android\Sdk`

### "API Level 31 not found"
Εγκατάστησε το Android 12.0 (API 31) από το SDK Manager του Android Studio.

### Build errors
Δοκίμασε:
```powershell
dotnet clean
dotnet restore
dotnet build -f net6.0-android
```
