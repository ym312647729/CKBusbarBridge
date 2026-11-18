"""只读导入原中压参数与测试操作；不复制模型、合同、人员或项目路径。"""
from pathlib import Path
import csv
import hashlib
import json
import sys
import subprocess

sys.stdout.reconfigure(encoding='utf-8')

root = Path(__file__).resolve().parents[1]
reference = Path(r'F:\RD\DQBusbarBridge')
schema = json.loads((root / 'CKBusbarBridge.DesignUI/PreviewRuntime/catalog/original-cabinet-ui.json').read_text(encoding='utf-8-sig'))
catalog = {row['key']: row['defaults'] for row in schema['scenarios']}

def tree(values):
    result = {}
    for path, value in values.items():
        owner = result
        for segment in path.split('.')[:-1]:
            owner = owner.setdefault(segment, {})
        owner[path.split('.')[-1]] = value
    return result

def set_value(owner, path, value):
    for segment in path.split('.')[:-1]:
        owner = owner.setdefault(segment, {})
    owner[path.split('.')[-1]] = value

sources = []
cases = []
def read_source(source_id, relative):
    path = reference / relative
    data = path.read_bytes()
    sources.append({'id': source_id, 'sha256': hashlib.sha256(data).hexdigest()})
    return data.decode('utf-8-sig')

archive = json.loads(read_source('model-parameters-47', 'docs/测试模型柜体驱动参数.json'))
for index, record in enumerate(archive['projects'], 1):
    scenario = {'FaceToFace': 'face-to-face', 'FaceToWall': 'face-to-wall', 'SideToSide': 'side-to-side'}.get(record.get('scenario'))
    original = record.get('parameters')
    parameters = {key: value for key, value in (original or {}).items() if key in ('General', 'ACabinet', 'BCabinet', 'Cabinet')}
    cases.append({'id': f'archive-{index:02}', 'source': 'model-parameters-47', 'sourceIndex': index,
        'scenario': scenario, 'orientation': record.get('savedCabinetOrientation'),
        'sourceStatus': record['status'], 'parameters': parameters or None,
        'sourceUnavailable': record.get('unavailableParameters', [])})
assert len(archive['projects']) == archive['expectedProjects'] == 47

matrix = json.loads(read_source('regression-matrix-43', 'tools/virtual-preview-test/matrix.generated.json'))
for record in matrix['cases']:
    parameters = tree(catalog[record['scenario']])
    for path, value in record.get('edits', {}).items():
        set_value(parameters, path, value)
    cases.append({'id': 'matrix-' + record['id'], 'source': 'regression-matrix-43',
        'sourceIndex': record['provenance']['row'], 'scenario': record['scenario'], 'parameters': parameters,
        'bridgeEdits': record.get('bridgeEdits', []), 'historicalOutcome': record['expect']['outcome'],
        'mapping': '原回归矩阵明确以原界面默认值及列出的编辑生成输入，保留原桥体编辑操作。'})

rows = list(csv.DictReader(read_source('historical-scenes-15', 'docs/validation/20261030-15场景/15场景测试参数.csv').splitlines()))
for record in rows:
    scenario = {'面对面': 'face-to-face', '面对墙': 'face-to-wall', '侧对侧': 'side-to-side'}[record['类型']]
    parameters = tree(catalog[scenario])
    wall = scenario == 'face-to-wall'
    numeric = {'楼高_mm': 'BuildingHeight' if wall else 'FloorHeight',
        '梁底_mm': 'FloorToBeamDistance' if wall else 'ParameterB',
        '柜间距或距墙_mm': 'CabinetToWallDistance' if wall else 'CabinetSpacing',
        '有符号偏心_mm': 'WallCabinetEccentricity' if wall else 'CabinetEccentricity',
        '每相根数': 'SinglePhaseRootCount', '相中心距_mm': 'PhaseSpacing' if scenario == 'face-to-face' else 'ExposedBusbarPhaseSpacing',
        '最小绝缘净距_mm': 'InsulationDistance', '绝缘子与支架总高_mm': 'InsulatorHeight'}
    for column, field in numeric.items():
        if record[column]: set_value(parameters, 'General.' + field, float(record[column]))
    parameters['General']['BusbarSpec'] = record['铜排规格_mm']
    if wall:
        parameters['General']['IsPhaseSwitched'] = record['换相'] == '是'
        for column, field in [('墙孔宽_mm', 'WallHoleWidth'), ('墙孔高_mm', 'WallHoleHeight'), ('墙孔下边距地_mm', 'WallHoleEdgeGroundHeight')]:
            if record[column]: set_value(parameters, 'General.' + field, float(record[column]))
    else:
        for side in 'AB': parameters['General'][side + 'CabinetExposedBusbarPhaseSequence'] = record[side + '柜相序']
    if scenario == 'side-to-side':
        parameters['General']['CabinetTopOpeningMode'] = 'LeftRight' if '左右开孔' in record['开孔'] else 'FrontRear'
    for cabinet, height_column in ([('Cabinet', 'A柜或单柜高_mm')] if wall else [('ACabinet', 'A柜或单柜高_mm'), ('BCabinet', 'B柜高_mm')]):
        for column, field in [(height_column, 'CabinetHeight'), ('柜宽_mm', 'CabinetWidth'), ('柜深_mm', 'CabinetDepth'), ('离地_mm', 'GroundHeight')]:
            if record[column]: set_value(parameters, cabinet + '.' + field, float(record[column]))
        prefix = cabinet + '.FrontHole'
        if scenario == 'side-to-side': prefix += '.Base1' if '左右开孔' in record['开孔'] else '.Base2'
        set_value(parameters, prefix + '.BaseHeight', float(record['柜端总基座高_mm']))
        set_value(parameters, prefix + '.BaseOpeningHeight', float(record['基座开孔高_mm']))
        if not wall and record['连接基座宽_mm'] != '读取原驱动':
            base_width = float(record['连接基座宽_mm'])
            if scenario == 'side-to-side' and '左右开孔' not in record['开孔']:
                set_value(parameters, prefix + '.BaseWidth', base_width)
            elif scenario == 'side-to-side':
                set_value(parameters, cabinet + '.FrontHole.L3', (base_width - 100) / 2)
        if scenario == 'face-to-face': set_value(parameters, prefix + '.ExtensionBaseHeight', 0)
    if wall: set_value(parameters, 'General.CabinetGroundHeight', float(record['离地_mm']))
    cases.append({'id': 'historical-' + record['编号'], 'source': 'historical-scenes-15', 'sourceIndex': record['编号'],
        'scenario': scenario, 'parameters': parameters, 'orientation': 'CabinetFrontToWall' if wall else None,
        'mapping': '保留表中所有明确数值；表中未给出的界面必填项沿用该场景已确认默认值，柜端总基座高写入对应类型总高。表中说明为读取原驱动的值没有伪造数值。',
        'historicalStopped': record['编号'] == '07'})
assert len(rows) == 15 and len(matrix['cases']) == 43
result = {'schema': 'bridge-reference-parameters/1', 'sources': sources, 'count': len(cases), 'cases': cases}
output = root / 'tools/verification/data/medium-voltage-reference-cases.json'
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(f'已导入 {len(cases)} 条：47 条模型参数、43 条回归用例、15 条历史场景。未复制任何模型或客户项目标识。')

def canonical(value):
    if isinstance(value, dict): return {key: canonical(item) for key, item in value.items()}
    if isinstance(value, list): return [canonical(item) for item in value]
    if isinstance(value, (int, float)) and not isinstance(value, bool): return float(value)
    return value

def case_key(case):
    return json.dumps(canonical({key: case.get(key) for key in ('scenario', 'orientation', 'parameters', 'bridgeEdits')}),
        ensure_ascii=False, sort_keys=True, separators=(',', ':'))

known = {case_key(case) for case in cases}
extra_cases, file_hashes = [], []
history_paths = subprocess.run(['rtk', 'proxy', 'rg', '--files', str(reference / 'tools/virtual-preview-test/runs'), '-g', 'case.json'],
    input=b'', stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True).stdout.decode('utf-8').splitlines()
for source_index, path in enumerate(sorted(history_paths), 1):
    data = Path(path).read_bytes()
    file_hashes.append(hashlib.sha256(data).hexdigest())
    record = json.loads(data.decode('utf-8-sig'))
    if record.get('schema') != 'virtual-preview-case/1': raise ValueError('历史用例格式不匹配。')
    parameters = tree(catalog[record['scenario']])
    for field, value in record.get('edits', {}).items(): set_value(parameters, field, value)
    candidate = {'id': f'history-run-{len(extra_cases) + 1:02}', 'source': 'historical-run-cases', 'sourceIndex': source_index,
        'scenario': record['scenario'], 'parameters': parameters, 'orientation': record.get('orientation'),
        'bridgeEdits': record.get('bridgeEdits', []), 'historicalOutcome': record.get('expect', {}).get('outcome')}
    if candidate['scenario'] == 'face-to-wall' and not candidate['orientation']:
        candidate['orientation'] = record.get('edits', {}).get('CabinetOrientation', 'CabinetFrontToWall')
    signature = case_key(candidate)
    if signature in known: continue
    known.add(signature)
    extra_cases.append(candidate)
extra = {'schema': 'bridge-reference-parameters/1', 'sources': [{'id': 'historical-run-cases',
    'rawRecords': len(history_paths), 'sha256': hashlib.sha256('\n'.join(sorted(file_hashes)).encode()).hexdigest()}],
    'count': len(extra_cases), 'cases': extra_cases}
(output.parent / 'medium-voltage-historical-extra-cases.json').write_text(json.dumps(extra, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(f'另核对 {len(history_paths)} 份历史运行数据，去重后新增 {len(extra_cases)} 条；基础及补充用例合计 {len(cases) + len(extra_cases)} 条。')
