# Work Time Widget

Windows 11용 출근/퇴근 시간 위젯입니다. 계정 로그인 시간을 기준으로 출근 시간을 기록하고, 예상 퇴근 시간과 근무 중 누적 수입을 간단히 확인할 수 있습니다.

## 주요 기능

- 오늘 출근 시간 표시
- 설정한 하루 근무시간 기준 예상 퇴근 시간 표시
- 오늘 날짜 출근 기록이 있으면 PC를 재부팅해도 기존 출근 시간 유지
- 설정에서 오늘 출근 시간을 수동 수정 가능
- 예상 퇴근 5분 전 Windows 시스템 알림 표시
- 연봉 기준 일일 누적 수입 표시
- 연도 변경 시 연 근무일수 자동 갱신
- 설정한 점심시간에는 누적 수입 증가 중지
- 수입 영역은 실행 시 기본 접힘 상태
- 시스템/라이트/다크 화면 모드 선택
- 항상 위에 표시 옵션
- 상단 영역 드래그로 위젯 이동

## 실행

가장 간단한 실행 방법:

```text
WorkWidget.exe
```

아이콘이 적용된 바로가기를 쓰려면:

```text
Work Time Widget.lnk
```

바로가기가 없다면 다음 스크립트를 실행하면 현재 폴더에 생성됩니다.

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\New-WidgetShortcut.ps1
```

로그인 시 자동 실행을 등록하려면:

```text
Install-StartupShortcut.bat
```

바탕화면 바로가기를 만들려면:

```text
Install-DesktopShortcut.bat
```

## 설정

오른쪽 위 설정 버튼에서 다음 값을 바꿀 수 있습니다.

- 출근시간
- 연봉
- 하루 근무시간
- 연 근무일수
- 점심 시작/종료 시간
- 화면 모드: 시스템, 라이트, 다크
- 항상 위에 표시

설정 저장 위치:

```text
settings.json
```

`settings.json`은 `~\Users\USER\AppData\Roaming\WorkTimeWidget`에 우선적으로 저장됩니다.

점심시간 기본값은 `12:00`부터 `13:00`까지입니다. 이 시간대에는 누적 수입이 증가하지 않으며, 초당 수입 계산은 `하루 근무시간 - 점심시간`을 기준으로 합니다.

`연 근무일수`는 설정 파일의 `WorkDaysYear`와 함께 관리됩니다. 연도가 바뀌면 해당 연도의 월~금 평일 수로 자동 갱신됩니다. 한국 공휴일은 별도로 차감하지 않습니다.

## 출근 기록

출근 기록은 실행 폴더 아래에 월별 CSV로 저장됩니다.

```text
attendance/
  2026/
    06.csv
```

연도가 바뀌면 현재 날짜 기준으로 새 연도 폴더가 자동 생성됩니다. 예를 들어 2027년에 실행하면 `attendance/2027/01.csv`처럼 기록됩니다.

CSV 형식:

```csv
날짜,출근시간,예상퇴근시간;시스템시작시간
2026-06-12,08:51,17:51; 08:54
```

CSV는 한글이 깨지지 않도록 UTF-8 BOM으로 저장됩니다.

## 출근 시간 기준

오늘 날짜 기록이 이미 있으면 해당 CSV 값을 우선 사용합니다.

예를 들어 `08:53` 출근 기록이 있는 상태에서 PC를 재부팅하고 `09:22`에 위젯을 다시 실행해도, 출근 시간과 예상 퇴근 시간은 `08:53` 기준으로 유지됩니다.

오늘 기록이 없을 때만 Windows 세션 로그인 시간을 감지합니다. 감지 순서는 다음과 같습니다.

```text
quser.exe -> explorer.exe 시작 시간 -> 현재 시간
```

## 빌드

이 앱은 C# WPF 기반이며 Windows 기본 .NET Framework 컴파일러로 빌드합니다.

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\Build-WorkWidgetExe.ps1
```

빌드 결과:

```text
WorkWidget.exe
```

앱 아이콘은 다음 파일을 사용합니다.

```text
assets/work-widget.ico
```

## 파일 구성

핵심 파일:

```text
WorkWidget.cs
Build-WorkWidgetExe.ps1
assets/work-widget.ico
assets/work-widget.png
```

배포 편의 파일:

```text
WorkWidget.exe
Run-WorkWidget.vbs
Run-WorkWidget.bat
New-WidgetShortcut.ps1
Install-DesktopShortcut.bat
Install-DesktopShortcut.ps1
Install-StartupShortcut.bat
Install-StartupShortcut.ps1
```

백업/참고 구현:

```text
work-widget.ps1
```

Git에 포함하지 않는 것을 권장:

```text
settings.json
.work-widget/
attendance/
*.lnk
WorkWidget.zip
assets/options.png
```

## 알림

예상 퇴근 시간 5분 전 Windows 시스템 알림을 표시합니다. 앱에서는 별도 사운드를 재생하지 않습니다.

Windows 알림 설정에서 앱 알림이 꺼져 있으면 알림이 보이지 않을 수 있습니다.
