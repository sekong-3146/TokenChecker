# TokenChecker

TokenChecker는 ChatGPT Work/Codex와 Claude Code의 구독 사용량을 Windows 시스템 트레이에서 확인하는 로컬 유틸리티입니다.

## 요구 사항

- 실행 환경: Windows 10 22H2 x64 이상
- 개발/빌드 환경: .NET SDK 10.0.400
- 선택한 종류의 Codex CLI 또는 Claude Code가 설치되어 있어야 합니다. 계정 로그인은 설정창에서 진행합니다.

## 계정 설정

1. 트레이 아이콘을 오른쪽 클릭하고 **설정**을 엽니다.
2. 확인할 에이전트에서 Codex 또는 Claude를 골라 **추가**합니다. 합계 최대 4개이며, Codex 4개나 Codex 3개 + Claude 1개처럼 자유롭게 구성할 수 있습니다.
3. 각 행에 구분할 이름을 입력하고 **로그인**을 눌러 브라우저에서 원하는 계정으로 로그인합니다.
4. **저장**을 누르면 해당 계정들의 사용량을 확인합니다. 사용 체크를 끄면 일시 중지하고, 제거하면 확인 대상에서 제외합니다.

로그인 중에는 **로그인 취소**로 중단할 수 있습니다. 실패하거나 취소하면 기존 계정 설정을 유지합니다. 설정창의 **취소**는 저장하지 않은 변경을 버립니다.
기존 설정은 Codex·Claude 기본 CLI 계정으로 유지됩니다. 설정창에서 다시 로그인하면 TokenChecker 전용 계정으로 전환됩니다.

## 요금제 표시

각 계정의 한도 바 위에 요금제를 표시합니다. Codex는 조회 응답의 `planType`, Claude는 해당 계정의 CLI 로그인 정보에 있는 `subscriptionType`을 사용합니다. 정보가 없거나 지원하지 않는 값이면 **확인 불가**로 표시합니다.

Codex Pro 계정은 요청된 표시 방식에 따라 **주간 한도만** 보여줍니다. 툴팁과 트레이 아이콘도 일반 Codex의 주간 한도를 사용하며, Spark의 별도 한도를 섞지 않습니다. Codex Plus와 Claude Pro 등 나머지 계정은 기존 5시간·주간 표시를 유지합니다. 이는 앱의 표시 규칙이며 모든 Pro 계정의 서비스 정책을 단정하는 의미는 아닙니다.

## 빌드

저장소 루트에서 실행합니다.

```powershell
dotnet build TokenChecker.sln -c Release
```

## 게시

.NET 런타임이 설치되지 않은 Windows x64에서도 실행할 수 있는 단일 파일을 생성합니다.

```powershell
.\publish.ps1
```

출력 파일: `artifacts\publish\TokenChecker.Tray.exe`

## 파일 위치

앱 데이터는 다음 경로에 저장됩니다.

- `%LOCALAPPDATA%\TokenChecker\settings.json`
- `%LOCALAPPDATA%\TokenChecker\usage-cache.json`
- `%LOCALAPPDATA%\TokenChecker\diagnostics.log`
- `%LOCALAPPDATA%\TokenChecker\accounts\<계정 ID>\codex`
- `%LOCALAPPDATA%\TokenChecker\accounts\<계정 ID>\claude`

## 보안

- 새 계정의 로그인 정보는 각 CLI가 위 계정별 폴더에 저장합니다. 기존 기본 CLI 계정의 로그인 파일은 덮어쓰지 않습니다.
- 사용량 조회는 로그인 정보를 읽으며, 토큰은 설정 파일·사용량 캐시·진단 로그에 기록하지 않습니다.
- 계정 제거와 설정 취소는 확인 대상만 변경합니다. 이미 생성된 계정별 로그인 폴더를 자동 삭제하지 않습니다.
- 텔레메트리를 보내지 않습니다.
- 모델 생성 요청을 보내지 않습니다.

## 제한

브라우저에서 실제 계정 인증을 완료해야 사용량을 조회할 수 있습니다. 같은 종류의 계정도 각각 로그인해야 합니다.
2026-09-08 요금제 변경 검증: Release 빌드 경고·오류 0개, Core 테스트 72개 및 요금제 화면/통신 실행 검증 4개 통과. 실제 Codex Pro 로그인에서 요금제와 주간 한도 조회를 확인했습니다. 이전 설정창/로그인 검증은 10개 통과했으며, 실제 서로 다른 4개 계정의 OAuth 완료와 동시 실시간 조회는 미검증입니다.
