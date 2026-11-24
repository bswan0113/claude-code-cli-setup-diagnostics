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
