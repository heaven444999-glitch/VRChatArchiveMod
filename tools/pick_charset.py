# Builds the Bad Apple chatbox grayscale ramp: renders a pool of simple, common hanzi
# with a CJK sans font (Microsoft YaHei ~ closest to VRChat's TMP CJK fallback), measures
# each glyph's real ink coverage in its full-width cell, and picks 31 of them evenly
# spread across the brightness range. Level 0 is U+30FB ・ (katakana middle dot): VRChat
# COLLAPSES runs of real whitespace (U+3000 included) in the chatbox and the other
# invisible candidates (Hangul Filler, braille blank) render at the wrong width — the
# middle dot was the only glyph measured in-game (2026-08-20) that stays full-width
# while reading as near-black. 32 levels total — same trick as the original "Bad Apple
# in chinese characters" video (凑出来了三十几个简单字).
#
# Output: ressources/badapple.charset (UTF-8, one line, lightest -> darkest), embedded
# in the DLL and used by BadAppleModule as the default pixel palette.

import os

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "ressources", "badapple.charset")
FONT = r"C:\Windows\Fonts\msyh.ttc"
LEVELS = 32
CELL = 64

# Simple everyday hanzi, thin to dense (measurement decides the actual order).
POOL = (
    "一乙二十丁厂七人入八九几儿了力刀又三于干士土工才寸下大丈与万上小口山巾千川个久"
    "勺丸夕广门义之尸弓己子也女飞刃习马乡丰王井开天夫元无云专木五支不太犬区历友匹车"
    "巨牙屯比互切瓦止少日中冈贝内水见午牛手毛气升长什片化币斤爪反介父从今凶分公仓月"
    "氏勿欠风丹匀乌凤勾文六方火为斗心尺引丑巴孔队办以书玉刊示末未击打巧正扑功去甘世"
    "古节本术可丙左右石布龙平灭东卡北占业旧帅归且旦目叶甲申电号田由史只央兄叫另四生"
    "失禾丘付代仙们仪白仔他斥瓜乎令用甩印乐句册犯外处冬鸟务包主市立闪兰半头汉宁穴它"
    "写让礼必永司尼民出奶加召皮边发对台矛母幼丝式刑动寺吉扣考托老执地场耳共亚朽朴机"
    "权过臣再西压厌在有百存而页匠夸夺灰达列死成夹至此贞师尘尖光当早吐虫曲团同吊吃因"
    "吸吗回刚则肉网年朱先丢舌竹乔伟传休伍伏优伐延件任伤价份华仰仿伙自血向似后行舟全"
    "会合兆企众爷创肌朵杂危旬旨负各名多争色壮冲冰庄庆亦刘齐交次衣产决充妄闭问闯羊并"
    "关米灯州汗污江池汤忙兴宇守宅字安讲军许论农设访寻那迅尽导异孙阵阳收阶阴防如妇好"
    "她妈戏羽观欢买红级约纪寿弄麦形进戒远违运扶坛技坏找批走均投抗坑志块声把报却劫芽"
    "花芬苍芳劳克苏杆材村杏极李杨求更束豆两丽医辰励否还来连步坚旱盯呈时吴助县里呆园"
    "围呀吨足邮男困吵串员听吹吧别岗帐财针钉告我乱利秀私每兵估体何但伸作伯低你住位伴"
    "身皂佛近役返余希坐谷妥含邻岔肝免角条卵岛迎饭饮系言冻状亩况床库应冷这序弃忘闲间"
    "闷判灶弟汪沙汽沟没沉怀忧快完宋宏牢究穷灾良证启评补初社识词译君灵即层尿尾迟局改"
    "张忌际陆阿陈阻附妙妖妨努忍劲鸡驱纯纳纵纸纹奉玩环武青责现表规直林枝杯板松构述或"
    "画卧事刺枣雨卖矿码奔奇奋态欧妻转轮软到非叔肯齿些虎肾贤尚旺具果味昆国昌畅明易昂"
    "典固忠呼鸣呢岸岩罗岭凯败购图钓制知垂牧物乖刮秆和季委佳供使例侧凭货依的迫质欣征"
    "往爬彼径所舍金命斧爸采受乳贪念贫肤肺肿胀朋股肥服周昏鱼兔狐忽狗备饰饱变京享店夜"
    "庙府底剂郊废净盲放刻育闸闹郑券卷单炒炎炉法河油泊沿注泥波泽治性学宝宗定宜审宙官"
    "空实试郎诗肩房诚视话建居届刷屈承孤降限妹姑姐姓始驾参线练组细织终经贯黑墨鼎疆囊"
)


def coverage(font, ch):
    img = Image.new("L", (CELL, CELL), 0)
    d = ImageDraw.Draw(img)
    d.text((CELL // 2, CELL // 2), ch, fill=255, font=font, anchor="mm")
    px = img.getdata()
    return sum(px) / (255.0 * CELL * CELL)


def main():
    font = ImageFont.truetype(FONT, int(CELL * 0.9))
    seen = set()
    measured = []
    for ch in POOL:
        if ch in seen:
            continue
        seen.add(ch)
        measured.append((coverage(font, ch), ch))
    measured.sort()

    # 31 evenly spaced brightness targets across the measured range (+ the space = 32).
    lo, hi = measured[0][0], measured[-1][0]
    ramp, used = [], set()
    for i in range(LEVELS - 1):
        target = lo + (hi - lo) * i / (LEVELS - 2)
        best = min((m for m in measured if m[1] not in used), key=lambda m: abs(m[0] - target))
        used.add(best[1])
        ramp.append(best)

    charset = "\u30fb" + "".join(ch for _, ch in sorted(ramp))
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(charset + "\n")

    print(f"pool {len(measured)} chars, coverage {lo:.3f}..{hi:.3f}")
    print("ramp:", charset)
    for cov, ch in sorted(ramp):
        print(f"  {ch} {cov:.3f}")


if __name__ == "__main__":
    main()
