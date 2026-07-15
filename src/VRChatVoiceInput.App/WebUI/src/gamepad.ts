const buttonNames: Array<[number, string]> = [
  [0x0001, "D-pad Up"],
  [0x0002, "D-pad Down"],
  [0x0004, "D-pad Left"],
  [0x0008, "D-pad Right"],
  [0x0010, "Start"],
  [0x0020, "Back"],
  [0x0040, "Left Stick"],
  [0x0080, "Right Stick"],
  [0x0100, "Left Bumper"],
  [0x0200, "Right Bumper"],
  [0x1000, "A"],
  [0x2000, "B"],
  [0x4000, "X"],
  [0x8000, "Y"]
];

export function gamepadButtonNames(buttonMask: number): string[] {
  const names: string[] = [];
  let remaining = buttonMask & 0xffff;
  for (const [flag, name] of buttonNames) {
    if ((remaining & flag) !== 0) {
      names.push(name);
      remaining &= ~flag;
    }
  }
  if (remaining !== 0) names.push(`0x${remaining.toString(16).padStart(4, "0").toUpperCase()}`);
  return names;
}
