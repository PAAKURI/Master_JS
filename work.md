# PAAKURI 작업 인수인계

마지막 갱신: 2026-07-21
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

- 호스트가 방을 만들고 참가자가 `주소:24872`로 참가한다.
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
