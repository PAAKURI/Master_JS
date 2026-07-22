# PAAKURI 작업 인수인계

마지막 갱신: 2026-07-22
환경: Godot 4.7.1 Mono, .NET 8, Windows
클라이언트: `C:\Users\user\orca\workspaces\Master_JS\dev`
분리 서버: `C:\Users\user\orca\workspaces\Master_GJ`

## 이번 작업 결과

AI 대전을 유지하면서 Godot High-level Multiplayer API와 `ENetMultiplayerPeer` 기반의 1대1 호스트 권한형 P2P(리슨 서버) 모드를 추가했다. 공식 Godot 문서 권고대로 참가자는 입력과 조준만 보내며, 호스트가 이동, 총알 생성/충돌, 패링, 피해, 사망, 라운드 점수를 결정한다.

별도 `Master_GJ` Git 저장소에는 UI/렌더링이 없는 Godot C# 헤드리스 권한 서버 프로젝트를 만들었다. 현재 플레이 가능한 경로는 `Master_JS`의 P2P 리슨 서버이며, `Master_GJ`의 2클라이언트 전용 서버 포맷과 클라이언트의 직접 호환은 후속 작업이다.

## 기술 선택

- 전송 계층: ENet UDP
- Godot API: `ENetMultiplayerPeer`, `MultiplayerAPI`, C# `[Rpc]`
- 중요 로비 메시지: reliable, channel 2
- 입력: unreliable ordered, channel 0
- 권한 스냅샷: 30Hz unreliable ordered, channel 1
- 호스트 peer ID: Godot 기본값 1
- 접속 식별자: 현재 `IP:포트`, 기본 UDP 24872

ENet을 선택한 이유는 Godot 4.7.1에 내장되어 별도 네이티브 플러그인이 없고, 저지연 UDP와 ordered/reliable 채널을 함께 제공하며 C# RPC와 직접 통합되기 때문이다. 인터넷 P2P는 NAT를 자동으로 해결하지 않으므로 포트 포워딩, UPnP 또는 향후 릴레이가 필요하다.

## 클라이언트 프로젝트 변경

- `NetworkSession.cs`: AI/Host/Client 모드, ENet 방 생성·참가, READY, 연결/종료 처리, 입력 RPC, 스냅샷 RPC, CLI 테스트 옵션
- `PlayerCommand.cs`: 사람·AI·네트워크가 공유하는 입력 데이터와 범위/조준 정규화
- `GameManager.cs`: 호스트 전투 권한, 참가 입력 적용, 30Hz 스냅샷 생성/수신, 맵·플레이어·총알 복제, 참가자 보간
- `Player.cs`: 원격 입력 소스, 네트워크 replica 모드, 로컬 입력 캡처, 권한 상태 반영
- `Bullet.cs`: 네트워크 ID, 소유자 ID, 충돌 없는 클라이언트 replica
- `Start.cs`, `Scene/start.tscn`: AI 대전, 멀티 방 생성, IP:포트 참가, READY, 연결 상태 UI
- `GameOverPanel.cs`, `Scene/main.tscn`: 누락된 QUIT 버튼 예외 수정, 호스트 재대결, 참가자 독립 재시작 차단
- `EyeBall.cs`, `Scene/player.tscn`: 두 개의 검정 원형 `RigidBody2D` 눈, spring/damping/chaos 힘, 머리 추적과 28px 이탈 제한
- `project.godot`: `NetworkSession` autoload 등록

## 권한 경계

호스트만 실행하는 로직:

- 실제 `Player._PhysicsProcess` 이동/벽 이동/반동
- 실제 `Bullet` 생성과 `RigidBody2D` 충돌
- 패링, 피해, 체력, 탄약, 재장전, 사망
- 맵 선택, 카운트다운, 라운드 점수, 최종 결과

참가자가 실행하는 로직:

- 키보드/마우스 입력 캡처와 `PlayerCommand` 전송
- 호스트 스냅샷 수신
- 플레이어 위치 보간과 충돌 없는 총알 replica 표시
- 로컬 카메라/눈/UI 연출

입력 RPC는 등록된 상대 peer만 허용하고 이동값을 -1~1로 clamp하며 조준의 NaN/Infinity와 영벡터를 정리한다. 클라이언트가 위치/체력/탄약을 보내는 RPC는 없다.

## 로비와 연결 종료 정책

- 호스트는 기본 게이트웨이가 있는 활성 Wi-Fi/Ethernet의 IPv4를 자동 감지한다. 2026-07-21 최종 확인 시 개발 PC의 LAN 주소는 DHCP 갱신된 `192.168.1.29`다.
- ENet 서버는 모든 로컬 인터페이스에서 UDP를 수신하며 로비에는 `192.168.1.29:24872` 같은 LAN 방 코드를 표시한다. 기본 24872가 사용 중이면 24873부터 최대 24881까지 순서대로 사용 가능한 포트를 선택한다.
- 같은 공유기의 참가자는 로비에 표시된 `IP:포트` 그대로 참가한다. WARP/WSL 같은 gateway 없는 가상 어댑터는 자동 선택에서 제외한다.
- 양쪽이 READY일 때 호스트가 게임 씬 전환을 reliable RPC로 지시한다.
- 참가자 종료 시 호스트는 타이틀로 돌아간다.
- 호스트 종료 시 참가자는 타이틀로 돌아간다.
- 결과 화면 재대결은 현재 호스트만 시작할 수 있고 참가자는 호스트 스냅샷으로 새 경기를 받는다.
- 호스트 마이그레이션은 구현하지 않았다.

## `Master_GJ` 분리 서버

추가 파일:

- `Master_GJ.csproj`, `project.godot`, `Server.tscn`
- `ServerMain.cs`: 최대 2클라이언트, READY, 입력 검증, 60Hz 권한 시뮬레이션, 포물선 총알, 반동, 재장전, 단순 패링/피해/2승 경기, 30Hz 스냅샷
- `README.md`: 실행법과 네트워크 경계

중요 한계: 서버 이동/맵 충돌은 아직 임시 사각형 arena 모델이다. 네 맵의 실제 Godot 충돌과 모든 벽 이동을 공유하려면 `task.md`의 SERVER-001을 수행해야 한다. 또한 전용 서버는 2클라이언트 프로토콜이고 현재 `Master_JS` 리슨 서버는 호스트+1클라이언트 프로토콜이라 직접 호환되지 않는다.

## 검증 결과

2026-07-21 수행:

1. `dotnet build Master_JS.csproj --nologo`
   - 경고 0, 오류 0
2. 클라이언트 Godot headless editor import
   - C# 스크립트와 씬 import 성공
3. AI 전투 씬 180프레임 headless 스모크
   - 멀티 변경 관련 예외 없음
   - 기존 `GameOverPanel`에 씬상 QUIT 버튼이 없던 예외를 발견해 수정
4. 로컬 Godot 두 프로세스 네트워크 테스트
   - 호스트: UDP 24872 bind, remote peer 연결, READY 후 `GameManager ready: Host`
   - 참가자: 127.0.0.1 연결, READY 후 `GameManager ready: Client`
   - 약 10초 동안 RPC 체크섬, payload, 씬 전환, 관리 코드 예외 없음
   - LAN 경로 `192.168.1.29:24874`로 재검증해 Host/Client 양쪽 전투 씬 진입 성공
5. `dotnet build Master_GJ.csproj --nologo`
   - 경고 0, 오류 0
6. `Master_GJ` Godot headless editor import 및 UDP 24873 스모크
   - import 성공, `PAAKURI authoritative server listening on UDP 24873`
7. `git diff --check`
   - 양쪽 저장소 오류 없음

Windows headless 실행에서 Godot 자체 `get_system_dir` HRESULT 메시지가 두 번 출력되지만 프로젝트 관리 코드 예외는 아니며 실행 종료 코드는 0이었다.

## 테스트용 CLI

Godot 실행 옵션 뒤 사용자 인수 구분자 `--` 다음에 사용한다.

- 호스트: `--paakuri-host --paakuri-auto-ready`
- 참가자: `--paakuri-join=127.0.0.1:24872 --paakuri-auto-ready`
- AI: `--paakuri-ai`
- 전용 서버 포트: `--port=24873`

## 다음 에이전트가 알아야 할 위험

- 실제 서로 다른 PC/공유기/인터넷 환경 검증은 아직 하지 않았다.
- 참가자 이동은 30Hz 보간만 있고 client prediction/reconciliation이 없어 높은 RTT에서 조작 지연이 보일 수 있다.
- 입력/스냅샷 sequence tick과 rate limit이 아직 없다.
- 짧은 랜덤 방 코드는 매치메이킹 서비스 없이 구현할 수 없어 현재는 `IP:포트`를 방 코드로 사용한다.
- 포트 포워딩/UPnP/릴레이가 아직 UI에 없다.
- 물리 눈은 headless에서 로드됐지만 실제 렌더러에서 흔들림 세기와 머리 안 위치를 수동 확인해야 한다.
- 서버 저장소 커밋: `48c8c08 Implement authoritative Godot multiplayer server` (`origin/main` 푸시 완료)

## 2026-07-22 작업 요약: 시작 UI와 캐릭터 커스터마이징

### 변경 내용

- `Scene/start.tscn`
  - `marketing.png` 전체 화면 배경은 유지하되 셰이더로 색보정, 비네트, 하단 그라데이션, 미세 스캔라인을 적용했다.
  - 기존 중앙 VBox 메뉴를 상단 브랜드 + 하단 반응형 글래스 패널로 교체했다.
  - AI/커스텀/호스트/참가/READY/종료 동선을 세 구역으로 나누고 상태 안내를 별도 패널에 배치했다.
  - 네이비/시안/옐로 테마, 시스템 폰트 폴백, hover/pressed/disabled 스타일을 정의했다.
  - 캐릭터 커스터마이징 모달과 실시간 미리보기 영역을 추가했다.
- `Start.cs`
  - 새 노드 경로에 맞춰 로비 이벤트를 다시 연결했다.
  - 버튼 호버, 브랜드/패널 등장, 모달 열기/닫기 애니메이션을 구현했다.
  - 몸 색/눈 색/눈 모양/눈 물리 성향 편집, 기본값, 취소, 저장 동작을 구현했다.
  - `--paakuri-customization-smoke` 테스트 인자를 추가했다.
- `CharacterCustomization.cs`
  - `CharacterLook`, `EyeShape`, `EyePhysicsStyle` 모델과 값 검증을 추가했다.
  - 로컬 설정을 `user://character_customization.cfg`에 저장/복원한다.
  - 눈 모양별 폴리곤과 물리 프리셋의 spring/damping/chaos 값을 한곳에서 관리한다.
- `CustomizationPreview.cs`
  - 마케팅 캐릭터의 각진 몸/다리/입을 코드로 그리는 미리보기 Control이다.
  - 포인터 방향, 스프링, 감쇠, 불규칙 힘을 반영해 두 눈을 실시간 애니메이션한다.
- `Player.cs`, `EyeBall.cs`, `GameManager.cs`
  - 실제 캐릭터에 커스텀 색과 눈 폴리곤/색을 적용한다.
  - 눈의 `SpringStrength`, `Damping`, `Chaos`를 선택 프리셋으로 설정한다.
  - 호스트/클라이언트의 로컬·원격 플레이어 매핑과 AI 기본 상대 외형을 처리한다.
- `NetworkSession.cs`
  - 몸 RGB, 눈 RGB, 눈 모양, 눈 물리 성향을 reliable channel 2 RPC로 교환한다.
  - READY도 channel 2로 맞추고 READY 전 커스터마이징을 먼저 전송해 씬 전환 race를 막았다.
- `project.godot`
  - `CharacterCustomization`을 `NetworkSession`보다 먼저 로드되는 autoload로 등록했다.

### 검증 결과

2026-07-22 실행:

1. `dotnet build Master_JS.csproj --nologo`: 경고 0, 오류 0.
2. Godot 4.7.1 Mono headless editor import: 성공.
3. 시작 씬 120프레임 스모크: 성공.
4. `--paakuri-customization-smoke`로 모달 열기 + 미리보기 draw 180프레임: 성공.
5. AI 메인 씬 240프레임: `PAAKURI GameManager ready: OfflineAi`, 관리 코드 예외 없음.
6. 로컬 호스트/클라이언트 2프로세스: peer 연결, auto READY, 양쪽 `GameManager ready: Host/Client` 확인, RPC 오류 없음.
7. `git diff --check`: 오류 없음.

### 다음 에이전트 참고

- 최종 육안 QA는 Windows 잠금 해제 후 필요하다. 현재 headless 씬 파싱/그리기는 정상이나, 일반 렌더러에서 16:9·16:10·창 모드 배경 크롭과 커스터마이징 모달을 확인한다.
- headless PNG movie 캡처는 Godot dummy renderer 내부 signal 11로 실패했다. 프로젝트 C# 예외가 아니라 엔진 캡처 경로의 오류였다.
- 시스템 폰트 폴백 순서는 UI `Pretendard → Noto Sans KR → Malgun Gothic → Segoe UI`, 제목 `Bahnschrift → Arial Black → 한국어 폴백`이다.
- 네트워크 외형은 로비에서만 교환한다. 경기 도중 설정 변경은 현재 UX상 불가능하다.
- `Player.cs`와 `resources/parry_cooldown.svg.import`의 Git unmerged 표시는 이 작업 전부터 존재했다. `Player.cs` 작업 트리는 커스터마이징을 포함해 빌드되므로 내용을 보존한 채 index 충돌을 별도로 해결한다.

## 2026-07-22 작업 요약: 시작 UX 재구성, 메뉴 씬 분리, 실제 반동 테스트

### 사용자 동선

```text
start.tscn
├─ LOCAL  → 기존 AI 대전 main.tscn
├─ MULTI  → multiplayer.tscn → 방 생성/주소 참가/READY → main.tscn
├─ CUSTOM → customization.tscn → 저장 또는 취소 → start.tscn
└─ QUIT   → 게임 종료
```

스타트 화면은 모든 패널을 제거하고 중앙 세로 4버튼만 남겼다. 버튼의 네이비 바탕, 시안 테두리, hover/pressed 상태는 기존 디자인을 이어가되 글자를 흰색으로 통일하고 짙은 네이비 그림자를 추가했다. 마케팅 이미지에는 기존 색보정과 비네트를 유지했고 원본 이미지 하단 문구가 UI와 경쟁하지 않도록 하단을 더 어둡게 처리했다.

### 새 파일

- `MultiplayerMenu.cs`, `Scene/multiplayer.tscn`
  - 마케팅 배경 바깥색과 맞춘 짙은 네이비 독립 멀티 화면이다.
  - 중앙에 방 생성 → 로컬 주소 → 참가 주소 → 방 참가 → 준비 순으로 배치했다.
  - 로컬 주소는 읽기 전용 `LineEdit`라 드래그/복사가 가능하고 실제 선택 포트가 바뀌면 `HostRoomCode`를 표시한다.
  - `LobbyChanged`를 구독해 연결 상태, READY 가능 여부, READY 취소 상태를 갱신한다.
- `CustomizationMenu.cs`, `Scene/customization.tscn`
  - 전체 화면에 가까운 대형 커스텀 패널과 실제 물리 테스트장을 제공한다.
  - `SubViewport` 안에 `Scene/player.tscn`을 직접 인스턴스하고 실제 `Bullet`도 같은 viewport world에 생성한다.
  - 포인터가 테스트장 위에 있을 때만 실제 플레이어 입력을 켜 UI 색 선택 중 오발하지 않도록 했다.
  - 캐릭터가 테스트장 밖으로 이탈하면 자동 리셋하며 수동 `테스트 리셋`도 제공한다.
- `resources/ui_menu_theme.tres`
  - 시작/멀티/커스텀의 시스템 폰트 폴백, 흰 버튼 글자와 그림자, 네이비/시안 상태 스타일, 입력창/패널/슬라이더 스타일을 공유한다.

### 기존 파일 변경

- `Start.cs`, `Scene/start.tscn`
  - 기존 복합 로비 및 커스텀 모달 로직을 제거하고 네 개의 명확한 진입 버튼과 간단한 등장/hover 모션만 담당하도록 축소했다.
  - 기존 `--paakuri-customization-smoke` 인자는 새 커스텀 씬으로 전환하는 호환 경로로 유지했다.
- `CharacterCustomization.cs`
  - `EyePhysicsStyle`과 `CharacterLook.EyePhysics`를 제거하고 `RecoilLevel` 1~5를 추가했다.
  - 단계별 실제 반동은 105, 125, 145, 165, 185이며 기본값은 3단계/145다.
  - 눈의 스프링/감쇠/chaos는 기존 기본 Soft 값으로 고정했다.
  - 저장 키는 `eye_physics`에서 `recoil_level`로 변경됐다. 이전 설정 파일에는 이 키가 없으므로 최초 1회 기본 3단계로 마이그레이션된다.
- `Player.cs`
  - `ApplyCustomization()`에서 `_recoilStrength`를 갱신하고 `TryShoot()`가 이 값을 실제 캐릭터 속도에 적용한다.
  - `PreviewMode`, `ResetPreview()`를 추가했다. PreviewMode 총알은 `CurrentScene`이 아니라 플레이어의 viewport parent에 생성된다.
- `Bullet.cs`
  - 충돌 파티클을 총알의 parent에 생성해 일반 게임과 SubViewport 테스트장 모두 같은 world에서 정상 표시한다.
- `NetworkSession.cs`
  - 커스텀 RPC의 마지막 정수 필드를 눈 물리 enum에서 `RecoilLevel`로 변경했다. 필드 개수와 reliable channel 2 순서는 유지했다.

### 검증

2026-07-22 실행:

1. `.NET 8` 빌드: 경고 0, 오류 0.
2. Godot 4.7.1 Mono editor import: 성공.
3. `start.tscn`, `multiplayer.tscn`, `customization.tscn`, `main.tscn` headless smoke: 성공.
4. 일반 OpenGL 렌더러/Godot embedded game: 세 메뉴 화면의 정렬, 색, 한국어 폰트, 그림자 육안 확인.
5. 커스텀 테스트장: 실제 플레이어 좌클릭 발사 및 반동 이동 확인, 슬라이더 3→5단계 표시 변경 확인, 5단계 반동 재발사 확인.
6. 로컬 ENet 2프로세스: 호스트/클라이언트 연결, auto READY, 양쪽 게임 씬 진입, 커스텀 RPC 오류 없음.
7. `git diff --check`: 통과.

### 다음 작업자가 특히 확인할 점

- 사용자별 반동은 실제 이동 성능 차이다. 동일 무기/동일 능력치가 핵심 규칙이라면 반동 슬라이더를 시각 효과 세기로 재해석하거나 매치 시작 시 공통값으로 합의하는 설계가 필요하다.
- `CustomizationPreview.cs`는 더 이상 씬에서 사용하지 않는다. Git 충돌을 정리한 뒤 삭제 후보로 처리한다.
- `Player.cs`는 이전 작업부터 unmerged index 상태지만 worktree 내용은 현재 빌드되는 통합본이다. 절대 `checkout --` 또는 hard reset으로 덮어쓰지 않는다.
- 멀티 연결 실패 시 시작 화면으로 돌아가는 현재 정책과 멀티 화면에서 바로 재시도하는 정책 중 하나를 UX 기준으로 결정한다.
