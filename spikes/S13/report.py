import json,sys
d=json.load(open(sys.argv[1],encoding='utf-8'))
print('\n'.join(d['log']))
