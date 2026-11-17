import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {cabinetScenePath} from '../CKBusbarBridge.DesignUI/PreviewRuntime/Core/cabinet-binding.mjs';
import {deriveCabinetScene} from '../CKBusbarBridge.DesignUI/PreviewRuntime/Core/cabinet-scene.mjs';
import {objectDimensionBinding,prepareBusinessDimensionEdit} from '../CKBusbarBridge.DesignUI/PreviewRuntime/Core/business-dimensions.mjs';
import {createOriginalCabinetInput} from '../CKBusbarBridge.DesignUI/PreviewRuntime/Core/original-cabinet.mjs';

const templates=new URL('../CKBusbarBridge.DesignUI/PreviewRuntime/templates/',import.meta.url),cache=new Map();
async function readTemplate(path){
  if(!cache.has(path))cache.set(path,JSON.parse(await readFile(new URL(path+'.virtual.json',templates),'utf8')));
  return cache.get(path);
}

for(const [scenario,orientation,original,edited,distanceAxis] of [
  ['face-to-face','CabinetFrontToWall',3500,3075.5,0],
  ['face-to-wall','CabinetFrontToWall',1340,1490.5,0],
  ['face-to-wall','CabinetRearToWall',1340,1720.25,0],
  ['side-to-side','CabinetFrontToWall',1000,1184.75,2]
]){
  const wall=scenario==='face-to-wall',field=wall?'General.CabinetToWallDistance':'General.CabinetSpacing';
  const input=createOriginalCabinetInput(scenario);
  assert.equal(input.values[field],original,scenario+' 默认距离');
  input.values['General.InsulationDistance']=125;
  input.orientation=orientation;
  const template=await readTemplate(cabinetScenePath(scenario,input));
  const project={id:'spacing-regression',revision:1,scenario,
    objects:[{id:'scene',cabinetBinding:{scenario}}],cabinetInputs:{[scenario]:input}};
  for(const distance of [original,edited]){
    input.values[field]=distance;
    if(distance===edited){
      const cabinet=wall?'Cabinet':'ACabinet';
      input.values[cabinet+'.CabinetDepth']+=120;
      input.values[cabinet+'.CabinetWidth']+=80;
    }
    const scene=await deriveCabinetScene(template,'默认',scenario,input,readTemplate);
    const marks=scene.parameters.filter(parameter=>parameter.name===field);
    assert.equal(marks.length,1,scenario+' 间距标注数量');
    const [mark]=marks,[[start,end]]=mark.annotationEdges;
    assert.equal(mark.value,distance);
    for(const axis of [0,1,2])
      assert.ok(Math.abs(Math.abs(end[axis]-start[axis])-(axis===distanceAxis?distance:0))<1e-6,scenario+' 间距标注跨度或方向');
    const binding=objectDimensionBinding(project,'scene',field);
    assert.equal(prepareBusinessDimensionEdit(project,binding).fields.find(row=>row.name===field)?.value,distance);
  }
  console.log(scenario,orientation,original+' → '+edited+' mm');
}
