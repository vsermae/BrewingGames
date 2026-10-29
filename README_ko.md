# 심연의 메아리 · 방 기반 로그라이크 RPG

<p align="center">
  <img src="https://raw.githubusercontent.com/vsermae/BrewingGames/main/.github/assets/banner.png" alt="Abyssal Echo Banner" width="960" />
</p>

심연의 메아리는 심해를 향해 내려가는 것을 핵심으로 하는 방 기반 로그라이크 RPG입니다. 각 층을 탐험하며 방을 지나고, 적과 싸우고, 보물을 장비하며, 업그레이드를 하고, 최종적으로 15층에서 심연의 군주를 물리칩니다.

- 방 기반 지도와 무작위 이벤트
- 상태 이상, 버프, 디버프, 보스 메커니즘이 얽힌 턴제 전투
- 장비 강화와 빌드 최적화
- Godot 4 + C# 기반의 안정적인 구현
- 브라우저, Chromium, Electron 없이 Windows / Linux로 배포 가능

## 게임의 매력

### 1. 방 하나하나가 선택의 순간

각 층은 무작위로 생성되며, 문을 열 때마다 다음과 같은 상황이 나타납니다.

- 적 방: 턴제 전투
- 상자 방: 세 가지 보상 중 하나 선택
- 상점: 골드로 장비와 물약 구매
- 제단: 강화, 정화, 대가를 치르는 힘의 대가
- 이벤트 방: 결과가 바뀌는 선택지
- 보스 방: 층별로 등장하는 문지기

### 2. 전투는 단순한 타격 싸움이 아닙니다

심연의 메아리는 전략과 자원 관리가 핵심입니다.

- 공격 / 방어 / 스킬의 타이밍이 승패를 결정
- 중독, 화상, 동결, 기절, 방어력 저하 등 상태 이상이 결합
- 층마다 다른 메커니즘이 플레이 스타일을 변화시킴
- 보스전은 단순한 체력전이 아닌 적응과 준비가 요구됨

### 3. 장기 개발을 염두에 둔 구조

이 프로젝트는 Godot 4 + C# 기반으로 설계되어 로직과 렌더링을 분리했습니다.

- `Core/`와 `Systems/`는 Godot 의존성을 최소화
- 플레이어, 적, 장비, 이벤트는 데이터 기반으로 관리
- 자동 검증, 스모크 테스트, 스크린샷 검사, 벤치마크를 개발 흐름에 포함

## 바로 실행하기

### 요구 사항

- Godot 4.7.2 .NET
- .NET SDK 8

### 로컬 실행

```bash
cd godot
dotnet build
godot --path .
```

### 내보내기

```bash
cd godot
godot --headless --path . --export-release "Windows Desktop" build/win/AbyssEcho.exe
godot --headless --path . --export-release "Linux" build/linux/AbyssEcho.x86_64
```

## 핵심 요소

- 방 기반 절차적 진행
- 턴제 전투와 상태 이상 관리
- 장비와 인벤토리 최적화
- 층별 메커니즘과 보스 도전
- 데이터 중심 밸런싱으로 빠른 반복 개발
- Windows / Linux 데스크톱 배포

## 문서

- [README.md](README.md)
- [godot/README.md](godot/README.md)
- [godot/MIGRATION.md](godot/MIGRATION.md)
- [RELEASE_NOTES.md](RELEASE_NOTES.md)

---

심연으로 더 깊이 내려가 보세요. 생존하고, 적응하고, 결국 심연을 정복하세요.
