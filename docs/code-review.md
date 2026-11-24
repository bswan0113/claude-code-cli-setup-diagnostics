# 독립 설계 검토

2026-11-24 기준. [개발 계획](development-plan.md)과 [사용자 흐름](workflow.md)을 검토했다. 검토를 시작할 때 확인한 저장소에는 기획 문서와 흐름도가 있었다. 작성 중 앱 초기 파일이 추가됐으며, 이번 소견은 설계 문구와 공식 문서에 근거한다. 아래 실험과 인수 조건은 구현 후 확인할 항목이다.

P0 네 건은 해당 기능을 통합하기 전에 공통 계약으로 정한다. P1은 자동 설치와 인증 안내의 분기를 구현할 때 확정한다.

## P0 · 실행 파일, PowerShell 인자, 작업 폴더의 경계

- **근거** — [개발 계획 19~20, 28~29행](development-plan.md)은 입력 경로를 프로세스 작업 디렉터리로 넘기고 같은 셸에서 CLI를 식별하도록 한다. 셸과 호스트 조합, 프로필 적용, 별칭·함수·래퍼의 처리 기준은 열려 있다. .NET의 작업 디렉터리 동작은 `UseShellExecute` 설정에 따라 달라지며, `ArgumentList`의 인자 이스케이프는 PowerShell의 `-Command` 본문 해석까지 막아주지 않는다. PowerShell은 이름이 같은 실행 파일보다 별칭과 함수를 먼저 선택할 수 있다. [Microsoft .NET WorkingDirectory](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.workingdirectory?view=net-10.0) · [Microsoft .NET ArgumentList](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist) · [PowerShell 실행 인자](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_pwsh) · [PowerShell 명령 우선순위](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_command_precedence)
- **해결 기준** — 실제로 지원할 PowerShell과 시작 방식을 명시한다. 런처는 신뢰 가능한 절대 경로로 셸을 열고 사용자 경로를 작업 디렉터리 데이터로 전달한다. 고정 스크립트가 폴더를 확인하거나 다시 설정해야 할 때도 리터럴 경로로 처리한다. 선택 폴더의 실행 파일을 이름만으로 진단 후보로 삼지 않는다. 진단과 실행은 같은 셸 종류·프로필 설정·유효 환경·작업 위치를 사용하고, 실제 호출 직전에도 명령 종류와 대상 및 위치를 확인한다. 정책에 가려진 스크립트와 출처를 확인하기 어려운 명령은 별도 결과로 남긴다. 검증된 파일의 직접 실행 결과와 이름을 통한 셸 호출 결과도 각각 기록한다.

## P0 · 대화형 터미널과 CLI 종료 관측

- **근거** — [개발 계획 21, 28, 36행](development-plan.md)은 PowerShell 열림과 CLI 시작 실패·종료·관측 중단을 구분한다. [흐름 37~45행](workflow.md)의 재실행 성공 분기에는 이를 입증할 신호가 지정되어 있지 않다. Windows PowerShell 5.1은 호출 옵션에 따라 `Ctrl+C` 처리와 프로세스 종료 코드 전달이 달라진다. 따라서 바깥 PowerShell이나 터미널 호스트의 종료 코드만으로 CLI 시작 여부나 종료 원인을 판정할 근거가 부족하다. [Windows PowerShell 실행 옵션과 종료 코드](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_powershell_exe)
- **해결 기준** — 셸 시작 실패, 셸 시작, CLI 호출 실패, CLI 프로세스 시작 관측, CLI 종료 코드 수신, 명시적 취소, 관측 중단을 구분한다. 시작 증거로 어떤 이벤트를 신뢰할지 정하고, CLI 프로세스가 열린 뒤에도 대화 준비 상태는 별도 신호가 입증할 때만 표시한다. 필요한 상태를 대화형 표준 입출력을 유지한 채 요청별로 전달할 수 있는지 먼저 실험한다. 종료 코드는 그 코드의 주체를 함께 기록하고, 비정상 코드 하나만으로 시작 실패나 복구 원인을 확정하지 않는다. 창 닫힘, 시간 초과, 상태 채널 단절은 확보한 마지막 상태를 표시한다. CLI 종료를 확인하지 못했거나 설치·PATH 문제의 근거가 없으면 사용자의 수동 진단으로 이어진다.

## P0 · PATH 변경, 환경 갱신, 재시도와 동시성

- **근거** — [개발 계획 19, 22행](development-plan.md)과 [흐름 21, 33~42, 47행](workflow.md)은 동의 후 사용자 PATH를 바꾸고 새 환경에서 같은 폴더를 한 번 재실행하도록 한다. Windows 자식 프로세스는 기본적으로 부모 환경을 상속한다. 사용자 PATH 저장과 런처가 새로 연 셸의 유효 PATH 갱신은 별개의 관측이다. 현재 흐름에는 동의 화면이 열린 사이 다른 인스턴스나 설치기가 PATH를 바꾼 경우와 조치 도중 종료된 경우의 처리 기준이 없다. [Microsoft Win32 환경 변수](https://learn.microsoft.com/en-us/windows/win32/procthread/environment-variables)
- **해결 기준** — 변경 동의는 대상 사용자·설치 방식 또는 추가 경로·원래 실행 요청에 연결한다. 앱 인스턴스끼리는 같은 Windows 사용자 범위에서 설치와 PATH 변경을 직렬화한다. 변경 직전에 설치 상태와 저장된 사용자 PATH를 다시 읽는다. 이미 반영된 항목은 쓰기를 생략하고 재검사하며, 조건이 바뀌어 표시한 조치가 달라지면 현재 상태를 안내한다. 사용자 PATH에 항목을 추가할 때 기존 항목 순서와 변수 참조·값 형식을 보존한다. 외부 편집기가 앱의 잠금을 공유한다는 전제는 두지 않는다. 쓰기 전후 값을 다시 확인하고, 감지한 충돌은 기존 스냅샷으로 덮어쓰거나 전체 값을 복원하지 않고 멈춘다.
- **재실행 기준** — 갱신된 유효 PATH를 자식 셸에 반영하는 방법을 명시하고, 프로필 적용 후 진단 셸과 실행 셸에서 기대한 CLI 대상과 버전을 확인할 수 있어야 한다. 갱신 전 런처의 PATH를 그대로 상속하는 경우를 검증에 포함한다. 원래 폴더가 재실행 직전에도 유효한지 확인하고 원래 요청당 자동 재실행은 한 번만 소비한다. 앱을 다시 열었거나 설치기 종료 여부를 확인하지 못한 경우에는 현재 설치 상태부터 확인한다. 취소나 실패 뒤 설치·쓰기·실행을 자동 반복하지 않는다.

## P0 · 로컬 이력과 공유 진단의 정보 보호

- **근거** — [개발 계획 27, 29행](development-plan.md)은 로컬 이력의 전체 경로와 사용자 지정 `CLAUDE_CONFIG_DIR` 확인을 요구한다. [흐름 17, 45행](workflow.md)은 이력 병합과 진단 공유를 다루지만 GUI·로그·클립보드·저장 파일의 필드 범위는 정하지 않았다. 공식 CLI 문서에 따르면 지원 버전의 인증 상태 JSON에는 구성 디렉터리 경로가 나올 수 있다. CLI·설치기 출력 원문과 경로에 표시되는 계정명·고객명도 공유 경계에서 다뤄야 한다. [Anthropic CLI 명령](https://code.claude.com/docs/en/cli-reference)
- **해결 기준** — 내 컴퓨터의 최근 목록에는 폴더 경로와 최근 시각 등 필요한 메타데이터만 저장한다. 이력 파서는 대화 본문을 추출하거나 저장하지 않으며 `CLAUDE_CONFIG_DIR`는 데이터 경로로만 해석한다. 진단 수집은 버전·인증 상태 같은 짧은 확인 명령에 한정하고 원문은 구조화 결과를 만들 때만 사용한다. 대화형 세션에서 입력·출력 기록을 수집하지 않는다. 공유 요약에는 허용 필드로 정한 실패 단계·결과 코드·명령 유형·버전·인증 상태 등만 넣는다. 전체 환경 변수, 토큰, 계정 식별자, 원본 세션 행, CLI·설치기 원문, 개인 경로는 공유 기본값에서 제외한다. 경로가 필요한 로컬 화면과 공유용 복사·저장 출력은 각각 점검한다.

## P1 · 설치와 인증의 판정 근거

- **근거** — [흐름 29~32, 45~47행](workflow.md)의 설치 후보 없음과 로그인 필요 분기에는 미지원 버전·접근 실패·정책 차단을 다룰 기준이 필요하다. Anthropic은 Windows 설치 경로를 여러 방식으로 안내하고, 인증 상태 명령의 종료 코드와 버전별 출력 필드를 문서화한다. [Anthropic Windows 설치](https://code.claude.com/docs/en/setup#install-on-native-windows) · [Anthropic CLI 명령](https://code.claude.com/docs/en/cli-reference)
- **해결 기준** — 실행 가능한 기존 설치, 조사 범위 내 후보 없음, 후보는 있으나 실행 불가, 판정 불가를 구분한다. 이름 조회 실패나 경로 접근 실패만 확인했으면 설치한 적이 없다고 표시하지 않는다. 자동 설치에는 출처·대상·권한 범위가 확인된 공식 경로와 고정된 인자만 사용한다. 설치기 성공 코드 뒤에도 새 환경에서 실제 CLI를 검증한다. 인증 상태는 지원 명령의 기대 결과 형식과 종료 코드를 함께 확인해 판정한다. 형식 불일치, 미지원 버전, 시간 초과, 정책 차단은 관측된 상태로 남긴다. `claude --version` 성공은 명령 호출 가능 상태로 기록하고 인증이나 연결 상태는 별도로 표시한다.

## 구현 후 검증 항목

| 영역 | 재현 조건 | 통과 관측 |
|---|---|---|
| 작업 폴더와 명령 선택 | 일반 사용자 Windows 11 x64에서 한글·공백·대괄호·앰퍼샌드·작은따옴표·%가 있는 경로, 긴 경로, 삭제·접근 거부 폴더, 지원하기로 한 UNC 경로를 시험한다. 폴더 안의 동명 파일, 프로필의 별칭·함수·작업 폴더 변경, 복수 설치도 재현한다. | 폴더 경로가 리터럴 데이터로 유지된다. 실제 CLI 작업 폴더와 실행 대상이 진단과 일치한다. 접근 실패와 대상 불일치는 각 단계에서 중단되고 선택한 폴더가 임의로 바뀌지 않는다. |
| 대화형 실행과 종료 | 지원 터미널에서 키보드 입력, 정상 종료, `Ctrl+C`, 창 닫기, CLI가 시작 전에 실패하는 경우, 시작 직후 비정상 종료, 상태 전달 끊김을 각각 재현한다. | 대화 입력이 유지되고 종료 코드의 주체를 확인할 수 있다. 정상 종료·창 닫힘·관측 중단으로 자동 복구가 시작되지 않으며, 일찍 종료된 프로세스가 대화 준비 완료로 표시되지 않는다. |
| 환경 갱신과 동시성 | 오래 열린 런처에서 PATH를 복구한다. 같은 사용자로 두 앱을 열어 중복 요청을 보내고, 동의 대기 중 외부 PATH 편집과 설치 중단·앱 재시작도 재현한다. 기존 PATH에 변수 참조와 대소문자가 다른 동일 경로를 포함한다. | 이미 설치됐거나 추가된 항목에는 중복 변경이 없다. 기존 PATH와 값 형식이 보존된다. 새 셸에서 기대한 CLI가 해석되고 원래 폴더에서 최대 한 번 자동 재실행한다. 감지한 충돌이나 갱신 실패는 중단 상태로 표시된다. |
| 진단 분류와 공유 | 정상·미지원·실패 인증 응답, 정책 차단, 손상 기록, 사용자 지정 구성 디렉터리를 사용한다. 가짜 토큰·계정명·민감 경로를 입력과 진단 응답에 심고 화면·복사 내용·파일·로그를 확인한다. | 실패 이유가 증거에 맞게 표시된다. 최근 목록과 로컬 경로 표시 범위를 확인할 수 있고 공유 요약·로그에 가짜 비밀값·원문·개인 경로가 남지 않는다. |

첫 통합 전에 터미널 관측을 먼저 실험한다. 셸 열림 이후 CLI 시작·종료를 안정적으로 입증할 수 없는 조합에서는 화면의 판정 범위를 관측 가능한 단계까지만 두고 수동 진단을 제공한다. PATH 쓰기와 자동 설치는 각 변경의 중복 방지, 최신 환경 재확인, 재실행 종료 조건을 실제 Windows에서 재현한 뒤 연결한다.

# 구현 코드 최종 재검토

2026-11-24 · 통합 소스의 앱, 런처, 진단·복구, 최근 폴더와 세션 재개를 정적으로 검토했다. 마지막 재검토에서는 동일 GUID의 정상본 우선 선택과 이동한 최근 폴더의 목록 유지를 코드에서 확인했다.

## 알려진 제한

### 1 MiB를 넘는 JSONL 행은 세션 제목을 읽는 데 사용하지 못한다

바이트 제한을 초과한 행은 JSON 유효 여부와 무관하게 읽기 실패로 분류한다. 첫 메시지에 큰 이미지와 짧은 텍스트가 함께 들어 있는 정상 JSONL이라도 행 전체가 제한을 넘으면 제목을 얻지 못하고, 프로그램은 해당 세션을 폴백 대상으로 분류한다. 다른 행에서 cwd를 확인하면 해당 폴더의 CLI 선택기를 사용할 수 있다. [행 크기 제한과 처리](../src/History/SessionHistory.cs#L130) · [읽기 실패 결과](../src/History/SessionHistory.cs#L178)

### PowerShell 시작 실패와 CLI 시작 실패가 같은 결과로 표시된다

정상·오류 CLI 종료는 구분하지만, 셸을 만든 뒤 GUI가 받는 첫 결과는 셸 종료 후다. 부트스트랩의 CLI 프로세스 시작 예외와 런처의 PowerShell 시작 예외도 모두 `StartFailed`로 돌아온다. 사용자는 셸이 열렸는지와 CLI를 시작할 수 없었는지를 안내에서 구분할 수 없다. 결과가 기록되기 전에 창이 닫히면 관측 중단으로 처리한다. [CLI 시작과 상태 기록](../src/Launcher/TerminalLauncher.cs#L48) · [GUI 쪽 셸 대기와 실패 반환](../src/Launcher/TerminalLauncher.cs#L102) · [UI 실패 처리](../src/App/MainWindow.xaml.cs#L203)

## 해결 확인

- **동일 GUID의 정상본 유지.** 읽기에 실패한 사본은 제목이 비워진다. 같은 GUID에 정상 제목이 있는 사본이 있으면 `Transcript.IsPreferredTo`가 수정 시각보다 먼저 그 사본을 선택한다. 두 사본의 읽기 상태가 같으면 최신 수정 시각을, 시각도 같으면 경로 순서를 적용한다. 손상 사본이 더 최근이어도 정상본이 재개 목록에 남는다. [GUID별 사본 선택](../src/History/SessionHistory.cs#L44) · [손상 판정과 우선순위](../src/History/SessionHistory.cs#L177)
- **이동한 최근 폴더 유지.** 이전에 저장한 경로는 폴더 존재 여부로 거르지 않고 목록에 남긴다. 해당 항목을 누르면 기존 경로를 입력칸에 표시하고, 실행 직전 확인에서 찾을 수 없으면 폴더 오류를 표시한다. 이 분기는 CLI와 자동 복구를 시작하지 않아 사용자가 새 경로를 선택할 수 있다. [최근 폴더 로드](../src/History/FolderHistory.cs#L18) · [최근 폴더 클릭](../src/App/MainWindow.xaml.cs#L99) · [실행 전 폴더 확인](../src/App/MainWindow.xaml.cs#L157)

## 정적 재검토 결과

- 정상 주 세션은 설정 루트의 `projects` 아래 GUID 파일에서만 읽고, 파일 수정 시각·JSONL cwd·첫 사용자 텍스트를 사용한다. 빈 세션, 하위 에이전트, 메타와 도구 결과가 제외된다. 제품 소스에는 `history.jsonl` 참조가 없다. 시작 화면에서 제목과 cwd 확보 뒤 남은 레코드를 읽지 않는 동작은 확정된 제품 범위다. [세션 인덱스](../src/History/SessionHistory.cs#L27) · [첫 메시지 선별](../src/History/SessionHistory.cs#L262)
- 손상 파일에서 알아낸 cwd를 `FallbackFolders`으로 전달하고 같은 경로는 한 번 표시한다. 그 행을 누르면 해당 cwd에서 `claude -r`을 호출한다. 일반 선택기에서는 사용자가 입력한 폴더를 사용하거나 찾아보기를 연다. 정상 세션 행은 동일한 cwd와 정규화한 GUID를 `claude --resume <GUID>`에 전달한다. [복구된 경로와 정상 세션](../src/History/SessionHistory.cs#L82) · [일반 선택기와 복구된 폴더 실행](../src/App/MainWindow.xaml.cs#L124) · [정상 세션 클릭](../src/App/MainWindow.xaml.cs#L108) · [런처의 CLI 인자 구성](../src/Launcher/TerminalLauncher.cs#L48)
- 세션 제목은 로컬 목록·툴팁·접근성 이름에 사용한다. 진단 공유에는 번역된 결과 제목과 본문, 결과 코드, 관측된 CLI 종료 코드만 넣는다. 런처 임시 결과 파일도 상태와 코드만 기록한다. [세션 제목 표시](../src/App/MainWindow.xaml#L170) · [공유 복사](../src/App/MainWindow.xaml.cs#L346) · [런처 결과 데이터](../src/Launcher/TerminalLauncher.cs#L70)
- 초기 검토 뒤 런처와 진단이 프로필 없이 앱의 유효 PATH를 사용하도록 맞춰졌다. 사용자가 본 PATH 대상 재확인, 사용자별 복구 잠금, 쓰기 전후 비교, 로그인 후 같은 실행 요청 재시도가 코드에 반영됐다. [런처 시작 옵션](../src/Launcher/TerminalLauncher.cs#L102) · [동일 환경에서 먼저 진단](../src/Diagnostics/Diagnostics.cs#L103) · [복구 대상과 잠금](../src/Diagnostics/Diagnostics.cs#L27) · [PATH 값 확인](../src/Diagnostics/Diagnostics.cs#L303) · [로그인 재검사 경로](../src/App/MainWindow.xaml.cs#L255)
- [프로젝트 정의](../src/ClaudeLauncher.csproj), [Windows x64 게시 설정](../src/Properties/PublishProfiles/Windows-x64.pubxml), [앱 시작](../src/App/App.xaml.cs), [언어 선택](../src/App/UiLanguage.cs), [콘솔 프로세스](../src/Launcher/ConsoleProcess.cs), [폴더 경로 정규화](../src/Launcher/WorkingFolder.cs)를 읽었다. 네 언어 사전에서 동일한 113개 키를 확인했고 화면·코드에서 대조한 필수 키 83개, 중복·빈 값, 종료 코드 자리표시자와 XAML 이벤트 핸들러 참조 15개를 점검했다. PowerShell 검사 시작 실패·시간 초과·비정상 종료·출력 해석 실패의 상세 키도 네 사전에 있다. [영어](../src/App/Resources/Strings.en.xaml) · [한국어](../src/App/Resources/Strings.ko.xaml) · [간체 중국어](../src/App/Resources/Strings.zh-Hans.xaml) · [일본어](../src/App/Resources/Strings.ja.xaml)
- WinGet의 시간 초과 경로는 복구 잠금을 유지한 상태에서 루트 프로세스 종료를 제한 시간 내 다시 확인한다. 다음 설치에 앞서 같은 Windows 세션에 실행 중인 WinGet도 확인한다. [시간 초과 처리](../src/Diagnostics/Diagnostics.cs#L198) · [다음 설치 전 WinGet 확인](../src/Diagnostics/Diagnostics.cs#L169) · [프로세스 종료 확인](../src/Diagnostics/Diagnostics.cs#L251)

## 헤드가 전달한 E2E 증거

헤드는 Windows x64 단일 EXE에 합성 세션 기록을 연결해 실제 Claude 프로세스에 `claude --resume <GUID>`의 정확한 GUID와 `claude -r`이 각각 전달되는 것을 확인했다. 독립 검토에서는 이 실행을 직접 반복하지 않았다. 위 코드 지적과 사전 검사는 정적 증거다. 설치·PATH 변경과 깨끗한 Windows에서의 전체 배포 인수는 이번 재검토의 실행 범위에 포함되지 않았다. [세션 재개와 손상 폴백 인수 기준](acceptance-tests.md#L49) · [세션과 런처 인수 코드](../tests/Acceptance/SessionChecks.cs) · [인자 전달 인수 코드](../tests/Acceptance/LaunchChecks.cs)

# 승인 시안 WPF 구현 검토

2026-11-24 · 기본 화면, 좌우 사이드바와 설치·복구 모달의 통합 소스를 검토했다. 실행 요청별 세션 ID·폴더·종료 결과, 정확한 터미널 창 식별, 최근 5개와 실행 중 목록의 분리, 키보드 포커스 경로를 중점 확인했다.

## 수정본 재검토

| 지적 | 최종 소스 확인 |
|---|---|
| P1 · 외부 세션 조회 사이의 같은 GUID 중복 실행 | **해결 반영.** 세션별 예약을 먼저 잡고, 실제 실행 직전에 외부 상태를 다시 조회한다. 같은 GUID가 관측되면 새 프로세스 대신 확인된 터미널로 이동한다. CLI가 아직 활성 세션을 등록하지 않았거나 다른 앱이 동시에 시작하는 경우까지 잠글 수 있는 구조는 아니다. [예약과 실행 직전 확인](../src/App/MainWindow.xaml.cs#L227) · [이동 대상 재조회](../src/App/MainWindow.xaml.cs#L349) · [동일 GUID 동시 실행의 기록 영향](https://code.claude.com/docs/en/sessions#branch-a-session) |
| P2 · 같은 콘솔 안에서 대화를 바꾼 뒤 이전 GUID가 열리는 중으로 남음 | **해결 반영.** 콜백 전에는 최초 요청 GUID를 예약하고, 콜백 후에는 실행 중 터미널에서 관측한 현재 GUID를 사용한다. 최근 목록은 시작 예약과 현재 실행 ID를 각각 제외한다. [시작 예약 수명](../src/App/MainWindow.xaml.cs#L469) · [실행 중 ID](../src/App/MainWindow.History.cs#L131) · [최근 목록 제외](../src/App/MainWindow.History.cs#L196) |
| P2 · 진단 중 복구 패널의 초기 키보드 포커스 | **해결 반영.** 진행 문구에 초기 포커스를 주고 검사 결과가 나오면 닫기 버튼으로 이동한다. 모달이 닫힐 때 기존 포커스가 유효하지 않으면 사이드바의 새 대화 버튼 또는 기본 창의 폴더 입력으로 이동한다. [검사 시작·완료 포커스](../src/App/SetupPanel.xaml.cs#L107) · [포커스 가능한 진행 문구](../src/App/SetupPanel.xaml#L36) · [모달 종료 후 포커스](../src/App/MainWindow.xaml.cs#L414) |

## 해결 및 통합 확인

- **실행 요청 분리.** 전역 터미널 잠금 대신 요청 목록을 사용한다. 복구 큐는 각 요청의 원래 또는 실제 폴백 폴더와 실행 의도를 전달한다. 늦게 온 시작 콜백은 요청의 생존 여부와 시도 번호를 확인한다. CLI 오류 종료는 시작 실패와 다른 경로에서 표시된다. 활동 관측 작업의 예외도 실제 셸 종료 결과와 분리됐다. [요청별 실행](../src/App/MainWindow.xaml.cs#L227) · [종료 처리와 콜백](../src/App/MainWindow.xaml.cs#L292) · [관측 예외 분리](../src/Launcher/TerminalLauncher.cs#L161)
- **시각 단위.** 초기 코드의 Windows FILETIME과 Unix 밀리초 직접 비교는 해결됐다. 현재는 상태 파일의 프로세스 시작 문자열 토큰을 FILETIME과 비교한다. 토큰이 없으면 둘을 Unix 밀리초로 맞춘다. 설치 PC 메타데이터에서 이 단위와 형식만 확인했다. [상태의 프로세스 식별](../src/Launcher/LiveTerminal.cs#L217) · [공식 상태 필드](https://code.claude.com/docs/en/agent-view#list-sessions-as-json)
- **포커스.** 제품과 같은 콘솔 호스트와 프로세스 생성 플래그를 사용한 숨김 합성 검사에서 HWND 소유자는 PowerShell 자식이었다. 수정본은 직접 실행과 외부 발견 모두 확인된 Claude PID의 콘솔로 연결해 표시되는 HWND를 찾는다. 해당 창으로 이동할 수 없을 때 런처는 실패를 표시한다. ConPTY의 숨은 핸들만 있는 외부 Windows Terminal 탭은 이동 불가로 처리한다. 이 검토에서 실제 전경 전환은 재현하지 않았다. [콘솔 창 탐색](../src/Launcher/LiveTerminal.cs#L72) · [이동 실패 피드백](../src/App/MainWindow.xaml.cs#L203) · [ConPTY 핸들 동작](https://learn.microsoft.com/en-us/windows/console/getconsolewindow)
- **최근 실행과 실행 중의 분리.** 실행 중 목록은 PID와 GUID 중복을 제거하고, 최근 목록은 관측된 실행 중 ID와 시작 예약 ID를 제외한 뒤 최대 5건을 표시한다. 저장된 실행 시각이 파일 수정 시각보다 우선하도록 초기 정렬 P2도 해결됐다. 위의 이전 ID 문제는 동일한 콘솔 안에서 세션을 전환하는 경우에 남는다. [실행 중 목록](../src/App/MainWindow.History.cs#L131) · [최근 목록](../src/App/MainWindow.History.cs#L196) · [실행 시각 우선](../src/History/SessionRecency.cs#L12)
- **사이드바와 접근성.** 좌우 고정과 원래 창 모드 복원에 각각 버튼이 있고, 기본 화면의 카드와 트리를 재사용한다. 버튼 접근성 이름에 제목·동작·경로가 추가됐고 실행 중 카드에는 활동 상태도 포함된다. 모달 배경 비활성화, Tab 순환, Escape 처리는 확인했다. 네 언어 사전의 키 집합과 5개 XAML의 XML 구조, 이벤트 핸들러 참조를 정적으로 대조했다. [좌우 고정과 해제](../src/App/MainWindow.Layout.cs#L32) · [접근성 이름](../src/App/LauncherRows.cs#L24) · [트리의 접근성 이름](../src/App/LauncherRows.cs#L142) · [모달의 키보드 탐색](../src/App/SetupPanel.xaml#L1)
- **제목.** CLI 2.1.330 로컬 기록을 사용자가 제시한 예시 3건과 대조했다. 한 예시는 `custom-title.customTitle` 기록, 두 예시는 `ai-title.aiTitle` 기록에서 일치했고, 세 예시 모두 첫 사용자 기록 이후에 저장된 제목이었다. 개인 제목과 본문은 문서와 결과에 싣지 않았다. 구현은 지정 이름·AI 제목·요약·프롬프트 순으로 제목을 찾는다. [제목 선택](../src/History/SessionHistory.cs#L184) · [공식 선택기 표시 규칙](https://code.claude.com/docs/en/sessions#use-the-session-picker)

## 실행 증거와 범위

- 독립 검토에서는 숨김 합성 콘솔로 HWND의 소유 프로세스를 확인했다. 로컬 상태 메타데이터에서 시각 형식을 확인했고 대화 본문과 사용자 제목은 출력하지 않았다.
- 최신 XAML에서 네 언어의 159개 키 집합이 일치한다. `MoveLeft`·`MoveRight`를 포함한 참조 48개, `ToolbarCombo`를 포함한 정적 리소스 연결 35개, 이벤트 핸들러 이름 28개를 확인했다. 이 확인은 빌드된 리소스의 실제 로드나 UI 포커스 전환을 입증하지 않는다. [앱의 사전 병합](../src/App/App.xaml#L5) · [정적 스타일 선언](../src/App/Resources/MainWindowStyles.xaml#L3)
- QA의 0.2.0 중간 기록은 두 독립 테스트 콘솔의 작업 경로·종료와 상태 시각 비교를 통과로 적었다. 초기 EXE의 리소스 누락은 후속 소스에서 반영된 것을 확인했다. QA 환경은 HWND 전경 활성화까지 확인하지 못했고, 이 검토에서 최신 EXE의 실제 전경 전환과 키보드·스크린리더 인수를 반복하지 않았다. [별도 QA 중간 기록](acceptance-tests.md#L145)

# 테마·글자 크기·창 불투명도 최종 재검토

2026-11-24 · [Windows x64 최종 후보 EXE](../artifacts/publish/launcher-0.3.0-final/ClaudeLauncher.exe)의 SHA-256을 직접 계산해 `C5D32F02AB87E0140B89660C3D87E4E07C100F54E7205F72411AC1325FF6895D`와 일치함을 확인했다.

| 기존 P2 | 최종 상태와 증거 |
|---|---|
| 후보 EXE에 화면 보정 미포함 | **해소.** 검증 후보는 폴더명 전경색, 압축 제목, 버튼 포커스 테두리, 트리 선택 막대와 후속 콤보·메뉴 보정을 포함한 소스로 게시됐다. [폴더명](../src/App/MainWindow.xaml#L214) · [압축 제목](../src/App/MainWindow.Layout.cs#L98) · [포커스·선택 표식](../src/App/Resources/LauncherStyles.xaml#L3) |
| 콤보·수동 선택 메뉴 키보드 대비 | **해소.** 언어 콤보에 이중 포커스 테두리를 적용하고, 언어 목록과 수동 선택 메뉴에 4 DIP 항목 표식을 추가했다. GUI 담당의 합성 WPF 검사에서 두 테마의 키보드 진입과 방향키 강조를 통과했다. 불투명 팔레트의 표식과 강조 배경 대비는 라이트 4.96:1, 다크 5.89:1이다. [콤보 포커스](../src/App/Resources/MainWindowStyles.xaml#L70) · [목록 강조](../src/App/Resources/MainWindowStyles.xaml#L37) · [수동 메뉴 강조](../src/App/Resources/LauncherStyles.xaml#L162) |
| 두 목록이 찬 사이드바에서 트리가 0 DIP로 줄어듦 | **P2 해소.** 카드 높이 상한을 사이드바 전체 높이 범위에 적용한다. 같은 독립 격리 WPF 재현기에서 폭 450 DIP·글자 125%·목록 각 3건으로 높이 420·560·620 DIP의 트리를 각각 36.0·34.4·36.0 DIP로 실측했다. 세 높이 모두 하단 조작부가 창 안에 들어왔다. 560 DIP의 트리 내부 표시 높이는 32.8 DIP로, 33.6 DIP인 첫 행의 하단 0.8 DIP가 경계에 걸렸다. 420·620 DIP에서는 첫 행 전체가 들어왔다. [사이드바 간격](../src/App/MainWindow.Layout.cs#L100) · [카드 높이 상한](../src/App/MainWindow.Layout.cs#L148) |

처음 화면에서 공통 경고가 제거됐고, 손상 cwd가 알려진 폴더의 복구 버튼과 트리 메뉴의 수동 폴더 선택은 유지됐다. 네 언어 사전의 167개 키가 일치한다. 이전 독립 네이티브 검사에서 단일 모니터의 Windows 배율 125%로 좌우 고정·해제 후 80% 알파가 유지됐다. 이번 후보 EXE의 직접 확인은 해시 비교였으며, 위 레이아웃과 키보드 수치는 소스로 빌드한 격리 WPF 검사 결과다. 여러 배율의 모니터 간 이동은 미검증이다. [수동 폴더 선택](../src/App/MainWindow.xaml.cs#L122) · [폴더별 복구](../src/App/MainWindow.xaml#L252) · [고정·해제 시 불투명도 재적용](../src/App/MainWindow.Layout.cs#L36)

# 8단계 설치·복구 검토

2026-11-24 · 첨부 원본 `붙여넣은 텍스트.txt`와 WPF, 진단 및 복구 공급자, 네 언어 리소스를 대조했다. 원본 스크립트는 실행하지 않았다.

## 마지막 P2 재검토

| 우선순위 | 근거와 해결 기준 |
|---|---|
| P2 · 실행 정책의 서명 판정과 최종 승인 재확인 | **해소.** 고정된 Windows PowerShell 보안 모듈에서 `Get-AuthenticodeSignature`로 실제 서명 상태를 읽는다. 쓰기를 맡은 PowerShell이 승인 당시의 명령 경로·유효 정책·범위, 스크립트 SHA-256, `Zone.Identifier`의 영역 번호·SHA-256, 서명 상태를 모두 다시 대조한다. 하나라도 다르거나 그룹·프로세스 정책이 우선하면 쓰기를 중단한다. [서명과 파일 정보 수집](../src/Diagnostics/EnvironmentChecks.Policy.cs#L49) · [쓰기 직전 대조](../src/Diagnostics/EnvironmentChecks.Policy.cs#L96) · [승인 지문](../src/Diagnostics/EnvironmentChecks.Policy.cs#L228) |

## 원본 대조

| 원본 | 통합 소스 확인 |
|---|---|
| 1 · 환경 (234행) | Windows 빌드·아키텍처, 런처가 사용하는 Windows PowerShell의 실제 버전, 사용자 경로의 공백·비ASCII를 보고한다. [환경 검사](../src/Diagnostics/FullDiagnostics.cs#L51) |
| 2 · 실행 파일 (257행) | 기본 EXE와 WinGet 경로, 서명과 버전, `.new`·`.old`·버전 폴더의 서명된 백업을 확인한다. 백업 선택과 WinGet 사용자 설치가 분리돼 있다. 복원은 교체 전 임시 파일을 검사하고, 교체 직전 대상·파일 지문·활성 프로세스를 다시 대조한다. 종료는 PID·경로가 표시되는 별도 조치이며 동일 계정·세션·시작 시각과 실제 종료를 확인한다. [백업과 WinGet](../src/Diagnostics/InstallChecks.Files.cs#L14) · [복원 및 실패 후 복구](../src/Diagnostics/InstallChecks.Files.cs#L139) · [프로세스 종료](../src/Diagnostics/InstallChecks.Processes.cs#L108) |
| 3 · PATH (342행) | 시스템·사용자·프로세스 PATH의 명령 우선순위, 등록 누락과 오래된 프로세스 환경을 구분한다. 초기 검토에서 지적한 프로세스 PATH 전체 교체가 수정됐다. 저장 값과 프로세스 후보를 분리하고, 실제 상속 PATH에서 Claude 경로만 앞으로 옮긴 뒤 두 경로의 명령을 검증한다. [PATH 분기](../src/Diagnostics/InstallChecks.Environment.cs#L53) · [프로세스 항목 보존](../src/Diagnostics/InstallChecks.Environment.cs#L193) |
| 4 · 충돌 (367행) | 실제 PowerShell 검색 순서와 WindowsApps 별칭, npm 전역 설치를 표시한다. npm 제거는 승인된 prefix·패키지·shim을 확인하고 스크립트 실행을 끈다. 구형 `.claude\local`은 승인 목록의 일반 파일을 지우고 디렉터리와 재분석 지점은 보존한다. [검색 순서](../src/Diagnostics/InstallChecks.Environment.cs#L102) · [npm 제거](../src/Diagnostics/InstallChecks.Npm.cs#L31) · [구형 파일 정리](../src/Diagnostics/InstallChecks.Files.cs#L283) |
| 5 · 실행 정책 (447행) | 실제로 선택된 npm 스크립트와 유효 정책·범위를 대조한다. 그룹 정책이 우선하면 수동 안내를 표시하며 사용자 조치에서만 `CurrentUser → RemoteSigned`를 제안한다. 이 조치는 승인한 스크립트와 적용 직전에 확인한 스크립트의 서명·해시·영역 정보가 같은 경우에만 진행된다. [정책 분류](../src/Diagnostics/EnvironmentChecks.Policy.cs#L158) · [승인 재검사](../src/Diagnostics/EnvironmentChecks.Policy.cs#L200) |
| 6 · 환경변수 (479행) | API 키·토큰·API 주소의 설정 범위, CA·Git Bash 경로, Git 및 프록시를 확인한다. 사용자 값 수정은 정해진 변수만 대상으로 하고 원문 자격 증명·프록시 주소 없이 이름과 범위만 표시한다. 로컬 관리 설정에 같은 변수가 지정되면 수동 안내로 처리한다. [변수 검사·조치](../src/Diagnostics/EnvironmentChecks.cs#L35) · [로컬 관리 설정](../src/Diagnostics/EnvironmentChecks.cs#L190) · [Claude Code의 관리 설정 범위](https://code.claude.com/docs/en/managed-settings) |
| 7 · 네트워크 (558행) | 원본의 다운로드·API·로그인 세 호스트를 각각 검사한다. TLS·DNS·프록시·시간 초과와 HTTP 응답을 구분한다. 다운로드 HTML과 지역 제한 표시를 따로 판정하고 API와 로그인 응답은 도달 여부로 표시한다. [호스트와 판정](../src/Diagnostics/EnvironmentChecks.Network.cs#L22) |
| 8 · 정리 (601행) | 대상 목록과 용량을 표시한다. 정상 기본 EXE가 확인돼야 업데이트 잔여 파일을 정리하며 최신 `.old`를 보존한다. 다운로드 캐시와 자동 업데이트 변경은 별도 승인 항목이다. [잔여 파일과 캐시](../src/Diagnostics/InstallChecks.Files.cs#L208) · [업데이트 설정](../src/Diagnostics/InstallChecks.Environment.cs#L195) · [설치 방식별 업데이트](https://code.claude.com/docs/en/setup#update-claude-code) |
| 최종 확인 (645행) | 전체 조치 뒤 8단계를 새로 검사하고 빠른 결과도 재관측한다. 기존 실행 요청이 있고 빠른 검사까지 통과했을 때만 그 요청을 다시 열 수 있다. 실행 요청 없이 전체 조치를 한 뒤 빠른 화면에 이전 상태가 남던 초기 지적은 수정됐다. [재검사와 재시도](../src/App/SetupPanel.FullDiagnostics.cs#L137) |

## 승인과 검증 범위

- 기존 설치·PATH 세 조치와 전체 검사 조치는 별도 확인 화면에서 대상과 해결 방법을 표시한다. 취소는 적용을 호출하지 않는다. 진단과 빠른 조치가 같은 사용자별 잠금을 사용하며, 공급자가 승인 시점의 상태를 다시 읽어 조치를 결정한다. 입력 cwd는 절대 경로·존재 여부를 확인한다. [빠른 조치 확인](../src/App/SetupPanel.xaml.cs#L74) · [확정 호출](../src/App/SetupPanel.FullDiagnostics.cs#L64) · [잠금과 공급자 호출](../src/Diagnostics/FullDiagnostics.cs#L99)
- 화면에는 손으로 확인할 경로와 코드가 표시된다. 환경변수의 원문은 승인 비교용으로만 사용하고 공유용 요약에는 제목·본문·결과 코드·CLI 종료 코드만 넣는다. 전체 검사에서는 원본의 바탕화면 PowerShell transcript를 생성하지 않는다. [변수 승인 지문](../src/Diagnostics/EnvironmentChecks.cs#L154) · [공유 요약](../src/App/SetupPanel.xaml.cs#L169)
- 독립 검토에서 임시 출력 경로로 WPF 프로젝트 빌드 경고·오류 0개를 확인했다. 초기 설치 사전의 빈 해결 문구는 네 언어에서 채워졌다. 최신 소스의 공용·진단 리소스는 언어별 478개이며 빈 값·중복이 없고 설치·복구 XAML의 동적 참조 44개가 연결된다. [설치 사전](../src/App/Resources/InstallChecks.ko.xaml#L4) · [언어 사전 로드](../src/App/UiLanguage.cs#L90)
- 최신 WPF 빌드는 경고·오류 0개였다. 후속 독립 검사는 제품 PowerShell 코드에서 정책 조회를 합성 값으로 고정하고 정책 쓰기를 테스트 신호로 대체했다. 실제 Windows PowerShell에서 미서명 합성 파일의 `NotSigned`를 확인했다. 파일 바이트 변경, Zone 스트림 추가, 같은 ZoneId에서 스트림 내용 변경, 승인 지문의 서명 상태 불일치를 각각 거부했다. 영역 3도 쓰기 단계에 진입하지 않았으며, 변경이 없는 로컬 파일만 테스트 신호에 도달했다. 실제 사용자 정책은 변경하지 않았다.
- 구현 담당의 격리 WPF 검사는 기존 조치 3개의 확인·취소, 같은 키의 서로 다른 대상, 오래된 승인 거부, 450×420 DIP 화면의 스크롤을 확인했다. 환경 검사 담당은 합성 네트워크 응답과 비밀값 비노출을 확인했다. 이 독립 검토에서는 실제 사용자 정책·PATH·설치·파일 삭제를 실행하지 않았다. [인수 매트릭스](acceptance-tests.md#L220)
