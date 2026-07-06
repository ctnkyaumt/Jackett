import sys, re
if len(sys.argv) < 2: sys.exit(0)
with open(sys.argv[1], 'r') as f: c = f.read()
c = re.sub(r'^(pick) (9fe3306|c8ded2c|eddbd49|f7cb603|4a0061e|89a3243|5fa4c77|08684de|3bc42d9|ee7d5e4)', r'drop \2', c, flags=re.M)
with open(sys.argv[1], 'w') as f: f.write(c)
