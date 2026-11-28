# 7. 컴파일 타임 코드

비슷한 명령 여러 줄을 손으로 쓰거나 외부 스크립트로 만들 필요가 없습니다. MC#은 **컴파일할 때** 반복과 분기를 풀어 명령을 만들어 줍니다. 이 장의 핵심 질문은 하나입니다. **"이 값은 상수로 들어가나, 매크로가 되나?"**

## 한 가지 규칙

`asm $"…{x}…"`의 `{x}`는:

| `x`가 | 결과 | 버전 |
|---|---|---|
| 컴파일 타임 상수 | 글자 그대로 끼워 넣음 → 평범한 명령 | 모든 버전 |
| 런타임 값 (스코어, 계산 결과) | 함수 매크로 줄이 됨 | 1.20.2+ |

컴파일 타임 상수는 리터럴, `const`, 그리고 상수 인자를 받은 `[Inline]` 매개변수와 `Unroll`/테이블 반복 변수, 이것들만으로 된 계산입니다.

**`string`은 항상 컴파일 타임 상수입니다.** 런타임 문자열은 `NString`이라는 별도 타입이고, `string` 매개변수에 넘기면 타입 오류가 납니다. 따라서 `string` 매개변수를 명령에 넣어도 **절대 매크로가 되지 않습니다.**

## 예제: 형광등 8개 끄기

거의 같은 `fill` 8줄을 직접 쓰는 대신:

[`examples/t07_inline.mcs`](examples/t07_inline.mcs):

```csharp
static readonly int[] LampX = { 3, 7, 11, 15, 19, 23, 27, 31 };

[Inline] static void Lamp(int x, bool on)
{
    string block = on ? "sea_lantern" : "gray_stained_glass";
    asm $"fill {x} 70 10 {x} 70 12 minecraft:{block}";
}

public static void Main()
{
    foreach (int x in LampX) Lamp(x, false);
}
```

결과 (모든 버전에서 같음):

```
[t0] > fill 3 70 10 3 70 12 minecraft:gray_stained_glass
[t0] > fill 7 70 10 7 70 12 minecraft:gray_stained_glass
[t0] > fill 11 70 10 11 70 12 minecraft:gray_stained_glass
[t0] > fill 15 70 10 15 70 12 minecraft:gray_stained_glass
[t0] > fill 19 70 10 19 70 12 minecraft:gray_stained_glass
[t0] > fill 23 70 10 23 70 12 minecraft:gray_stained_glass
[t0] > fill 27 70 10 27 70 12 minecraft:gray_stained_glass
[t0] > fill 31 70 10 31 70 12 minecraft:gray_stained_glass
```

무슨 일이 일어났나:

1. `static readonly` 배열은 **테이블**입니다. `foreach`는 원소마다 본문을 복사합니다.
2. `[Inline]` 메서드는 함수가 되지 않고, 호출한 자리에 본문이 복사됩니다.
3. `x`와 `on`이 상수이므로 `on ? … : …`가 컴파일할 때 풀리고, `block`도 상수가 됩니다.
4. 명령 8줄이 **매크로 없이** 그대로 나옵니다. 데이터팩에는 이 8줄만 들어갑니다.

## 생성된 명령을 직접 보고 싶다면

시뮬레이터 출력의 `>` 줄이 곧 실행된 명령입니다. 파일로 보려면:

```sh
mcs t07_inline.mcs -o out/t07 --mc 1.21.4        # 버전 하나로 빌드하면 층도 하나
cat out/t07/data/t07_inline/function/main.mcfunction
```

매크로 줄은 `$`로 시작합니다 (`$say $(a0)`). `$`가 없으면 상수로 들어간 것입니다.

## 상수 인자와 런타임 인자

```csharp
[Inline] static void G(int x) { asm $"say {x}"; if (x > 3) asm say big; }

G(5);                  // → say 5 / say big            (if가 사라짐)
G(m.Random(0, 9));     // → 매크로로 say, 실행 중에 if 검사
```

같은 `[Inline]` 메서드라도 인자가 상수인지에 따라 결과가 달라집니다. 상수를 넘기면 분기가 사라지고, 런타임 값을 넘기면 평범한 코드처럼 동작합니다.

## 반복 도구

| 도구 | 쓰임 |
|---|---|
| `foreach (int i in Unroll.Range(0, 8))` | 0~7마다 본문 복사 |
| `foreach (var v in 테이블)` | 테이블 원소마다 본문 복사 |
| `Tbl[2]` (상수 인덱스) | 상수 |
| `Tbl[i]` (런타임 인덱스) | 스토리지에서 매크로로 읽음 (1.20.2+) |

소수도 상수로 계산됩니다: `{i * 0.25f}` → `0`, `0.25`, `0.5`, … ([4장](04-decimals.md)).

## 정리

- `{x}`가 상수면 그대로, 런타임 값이면 매크로. `string`은 항상 상수입니다.
- 테이블 + `foreach` + `[Inline]`으로 반복되는 명령을 만듭니다.
- 관련 명세: [13 컴파일 타임 평가](../spec.md#13-컴파일-타임-평가)

다음: [8. 테스트와 디버깅](08-testing.md)
