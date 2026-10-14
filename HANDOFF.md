# Handoff: Windows WattTray

Stan na 2026-10-14. Repozytorium: [r00m00n/watttray](https://github.com/r00m00n/watttray).
Branch roboczy: `feat/windows-system-tray`. Kontynuacja lokalnie na Windows z Codexem.

## Cel i stan projektu

WattTray to mała natywna aplikacja Windows pokazująca moc ładowania/rozładowania baterii w obszarze powiadomień. Docelowe środowisko: Windows 11. Stack: C#, .NET 10 (`net10.0-windows`), WinForms, bez zewnętrznych zależności aplikacji. Nie wracać do .NET 8.

Zweryfikowano aktualne pliki brancha podczas przygotowania dokumentu. Preview wcześniejszej rozmowy wskazuje commit `1a8864c` jako ówczesny HEAD ze zmianą użytkownika na .NET 10; przy wznowieniu sprawdzić aktualny HEAD i lokalne zmiany, nie resetować ani nie nadpisywać pracy użytkownika.

MVP zawiera:
- `src/WattTray/WattTray.csproj`: WinExe, .NET 10, WinForms, manifest.
- `src/WattTray/Program.cs`: punkt wejścia aplikacji.
- `src/WattTray/TrayApplicationContext.cs`: standardowa ikona, tooltip, menu Exit, odświeżanie co 2 sekundy i zwalnianie zasobów.
- `src/WattTray/Power/BatteryPowerReader.cs`: P/Invoke SetupAPI i Windows Battery Class API.
- `src/WattTray/Power/BatteryReading.cs`: Watts, podpisany RateMilliwatts oraz stan Unknown/Charging/Discharging/OnAc.
- `README.md`: opis MVP i uruchamiania.

## Dotychczasowy odczyt

`Read()` enumeruje obecne interfejsy baterii przez `SetupDiGetClassDevs`, `SetupDiEnumDeviceInterfaces` i `SetupDiGetDeviceInterfaceDetail`. Dla każdej ścieżki otwiera `SafeFileHandle` przez `CreateFile` (read/write, współdzielenie read/write, OPEN_EXISTING), pobiera tag przez `IOCTL_BATTERY_QUERY_TAG`, następnie status przez `IOCTL_BATTERY_QUERY_STATUS`.

Pierwsza bateria z poprawnym tagiem i dostępnym Rate daje wynik. Nie ma agregacji wielu baterii. Stan jest wyznaczany z flag PowerState, a prezentowana moc to `Math.Abs(status.Rate) / 1000.0`. Znak surowego Rate pozostaje w rekordzie.

Brak `IOCTL_BATTERY_QUERY_INFORMATION` i `BATTERY_INFORMATION`: aktualny kod zakłada jednostki mW, zamiast sprawdzać capability. To wymaga korekty przed uznaniem watów za wiarygodne.

## Aktualny problem

Użytkownik zgłosił na ThinkPadzie P1 tooltip **WattTray - power unavailable**. Tooltip ten oznacza, że `BatteryPowerReader.Read()` zwrócił null. Przyczyna nie została ustalona ani potwierdzona na sprzęcie.

Kod pomija błędy poszczególnych etapów bez raportu: nieudane otwarcie, brak taga, nieudany status lub `BATTERY_UNKNOWN_RATE` kończą się przejściem do kolejnej baterii. Błędy enumeracji również nie są ujawniane. Sam tooltip nie rozróżnia braku urządzeń od błędu API ani braku obsługi Rate.

Diagnostyka opisana poniżej jest planem, nie ukończoną implementacją. Ten handoff zmienia wyłącznie dokumentację. Najpierw zbieramy dane native API; nie dodajemy fallbacku na podstawie domysłów.

## Plan diagnostyki

Zwracać obok odczytu raport z etapem, wynikiem i powodem niedostępności. Umożliwić użytkownikowi odczyt/skopiowanie raportu lub zapis do znanej lokalizacji; WinExe nie gwarantuje widocznej konsoli.

1. **Enumeracja baterii:** zapisać architekturę procesu, liczbę interfejsów, indeks i ścieżkę urządzenia oraz wyniki każdego wywołania SetupAPI. Rozróżniać oczekiwany koniec enumeracji ERROR_NO_MORE_ITEMS (259), brak baterii i awarię. Przy zapytaniu o wymagany rozmiar bufora rozróżniać oczekiwany ERROR_INSUFFICIENT_BUFFER (122).
2. **Layout SetupAPI:** sprawdzić cbSize, wymagany rozmiar bufora i offset DevicePath. Aktualny kod używa `pathOffset = IntPtr.Size == 8 ? 8 : 4`. To konkretne podejrzane miejsce: rozmiar struktury/cbSize nie jest tym samym co offset pola. Z definicji DWORD + WCHAR[] wynika offset pola 4; zweryfikować go z nagłówkiem Windows SDK/layoutem i sprawdzić pełny początek ścieżki przed CreateFile. Jest to hipoteza na podstawie kodu, nie potwierdzona przyczyna na P1.
3. **CreateFile:** raportować ścieżkę, żądany dostęp/share mode, ważność handle i kod błędu. Nie zakładać z góry, że potrzebne są uprawnienia administratora.
4. **QUERY_TAG:** raportować powodzenie DeviceIoControl, bytesReturned, kod błędu i tag. Sukces z tagiem 0 rozróżniać od nieudanego wywołania.
5. **QUERY_INFORMATION:** dodać `BATTERY_QUERY_INFORMATION` z bieżącym tagiem i poziomem `BatteryInformation`, odczytać `BATTERY_INFORMATION`. Sprawdzić ABI struktur, rozmiary i długość odpowiedzi. Zapisać Capabilities (hex), zwłaszcza `BATTERY_CAPACITY_RELATIVE` i `BATTERY_SYSTEM_BATTERY`, oraz podstawowe dane pojemności/chemii.
6. **QUERY_STATUS:** raportować tag, wynik, bytesReturned, kod błędu oraz surowe PowerState (hex i flagi), Capacity, Voltage i podpisany Rate. Osobno oznaczać wartości unknown; `BATTERY_UNKNOWN_RATE` jest obecnie porównywany z int.MinValue.
7. **Interpretacja:** jeżeli `BATTERY_CAPACITY_RELATIVE` jest ustawiony, Rate ma jednostki względne, więc nie przeliczać go bezpośrednio na waty. Jeśli flagi nie ma, Rate jest w mW. Dodatni Rate oznacza ładowanie, ujemny rozładowanie; zachować znak w diagnostyce. Nieznany Rate nie oznacza zera. Voltage jest raportowane w mV, z uwzględnieniem sentinela unknown.
8. **Błędy:** pobierać `Marshal.GetLastWin32Error()` natychmiast po nieudanym wywołaniu z SetLastError=true, przed innymi wywołaniami native. Zapisać kod liczbowy i opis (np. Win32Exception). Nie traktować starego last error po sukcesie jako błędu tego wywołania. Raportować każdą baterię, nawet gdy inna zwróci poprawny odczyt.

Odczyt dotyczy przepływu energii baterii. Szczególnie na zasilaczu nie należy utożsamiać go z całkowitym poborem laptopa z gniazdka.

## Kontynuacja i test lokalny

Pracować na istniejącym branchu, najpierw sprawdzając lokalny stan:

```powershell
git status
git fetch origin
git switch feat/windows-system-tray
git pull --ff-only origin feat/windows-system-tray
dotnet --info
dotnet build .\src\WattTray\WattTray.csproj
dotnet run --project .\src\WattTray\WattTray.csproj
```

Jeśli istnieją lokalne zmiany lub branch jest rozbieżny, najpierw je zachować i ocenić; nie wykonywać reset --hard. Potrzebny jest SDK .NET 10 i rzeczywisty Windows z baterią. Udany build nie dowodzi poprawności odczytu native.

Po implementacji diagnostyki na P1:
- zebrać pełny raport na baterii w spoczynku i pod obciążeniem;
- porównać raport po podłączeniu zasilacza, podczas ładowania i przy pełnej baterii;
- sprawdzić zachowanie po odłączeniu zasilacza oraz uśpieniu/wznowieniu;
- potwierdzić poprawną enumerację, otwarcie urządzenia, niezerowy tag, capabilities i status;
- sprawdzić, że niedostępny/względny Rate ma jasny powód i nie jest pokazywany jako fałszywe 0 W;
- sprawdzić aktualizację tooltipu i Exit bez pozostawionej ikony.

Do raportu testowego dołączyć model komputera, wersję Windows, architekturę procesu, SHA testowanego commita i logi etapów. Nie uznawać hipotezy offsetu ani ograniczenia sterownika za rozwiązanie bez tych danych.

## Workflow i warunek PR

**feature branch → implementacja diagnostyki/naprawy → test lokalny na Windows → PR do main.**

Commitować kolejne zmiany na `feat/windows-system-tray`, zachowując zmiany użytkownika. PR utworzyć dopiero po lokalnej weryfikacji i udokumentowaniu wyniku na realnym sprzęcie. Teraz zadaniem jest wyłącznie commit handoffu; bez tworzenia PR, bez merge do main. Fallbacki i własne renderowanie ikony odłożyć do walidacji native API.

Kryterium gotowości: poprawny odczyt watów, gdy urządzenie udostępnia absolutny Rate, oraz jednoznaczna diagnostyka w pozostałych przypadkach. Opis PR powinien zawierać przyczynę, zmianę zachowania i wynik testów sprzętowych.

## Dokumentacja źródłowa

- [BATTERY_INFORMATION i BATTERY_CAPACITY_RELATIVE](https://learn.microsoft.com/en-us/windows/win32/power/battery-information-str)
- [BATTERY_STATUS: PowerState, Voltage i Rate](https://learn.microsoft.com/en-us/windows/win32/power/battery-status-str)
- [SP_DEVICE_INTERFACE_DETAIL_DATA_W](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/ns-setupapi-sp_device_interface_detail_data_w)

## Implementation update (2026-10-14)

The diagnostic implementation is now in `src/WattTray`:

- SetupAPI device paths are read at byte offset 4 on both architectures; `cbSize` remains 8 on x64 and 6 on x86.
- The reader reports enumeration, device path, handle open, query tag, query information, and query status results for every battery interface. Native failures include the Win32 code and message.
- `BATTERY_INFORMATION` capabilities are queried before a rate is shown as watts. Relative, unknown, or unit-ambiguous rates remain unavailable and are explained in the report.
- The tray tooltip also shows charge percentage from `BATTERY_STATUS.Capacity / BATTERY_INFORMATION.FullChargedCapacity`, including relative units where the ratio remains meaningful.
- The tray icon now renders the rounded watt value itself, with a small `W` unit marker and a state-colored outline; the tooltip retains the precise one-decimal reading.
- Right-click the tray icon and choose **Copy diagnostics** to copy the latest report.

Local .NET 10 build succeeded with zero warnings using an alternate output directory because the normal `bin` output was locked by a running WattTray process. No hardware result has been collected on the ThinkPad P1; this change improves diagnosis and corrects a confirmed path-offset bug, but it does not establish the P1 driver's reported values. Collect the copied report on the P1 before claiming the watt reading is fixed or opening a PR.
