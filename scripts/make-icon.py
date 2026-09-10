from pathlib import Path
from PIL import Image, ImageDraw

target = Path(__file__).resolve().parents[1] / 'src' / 'CheckCheck.App' / 'Assets' / 'checkcheck.ico'
target.parent.mkdir(parents=True, exist_ok=True)
im = Image.new('RGBA', (256, 256), (0, 0, 0, 0))
d = ImageDraw.Draw(im)
d.rounded_rectangle((8, 8, 248, 248), 57, fill='#24664F')
d.line([(48, 135), (85, 172), (150, 92)], fill='#FFFFFF', width=19, joint='curve')
d.line([(117, 146), (145, 174), (210, 94)], fill='#BCD9BE', width=17, joint='curve')
im.save(target, sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
print(target)
