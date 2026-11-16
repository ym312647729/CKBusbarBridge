import {DefinitionError} from './expressions.mjs';
import {driveStructureCoordinates} from './structure-coordinates.mjs';
import {nativeSceneToPreview} from './structure-installation-preview.mjs';
import {swMatrix,inverseRigidMatrix,multiplyMatrix,transformPoint} from './geometry.mjs';
const one=(rows,label)=>{if(rows.length!==1)throw new DefinitionError(label+'必须唯一，实际'+rows.length);return rows[0];};
export async function readUpstreamStructure(model,design,read){
  const source=await read(model.templatePath),original=source.configurations.find(row=>row.name===model.configuration),configuration=structuredClone(original),graph=one(model.structure.models.filter(row=>row.templatePath===model.templatePath&&row.configuration===model.configuration),'基座受驱尺寸');
  if(!driveStructureCoordinates(original,configuration,graph))throw new DefinitionError('基座安装坐标未求值');
  const installed=one(design.installation.preview.instances.filter(row=>row.instanceId===model.modelId),'基座安装位置'),pose=multiplyMatrix(inverseRigidMatrix(nativeSceneToPreview),installed.transform),coordinate=name=>swMatrix(one(configuration.features.filter(row=>row.type==='CoordSys'&&row.name===name&&!row.suppressed),name).definition.properties.Transform.transform);
  return {model,pose,coordinate,point:name=>transformPoint(coordinate(name).slice(12,15),pose)};
}
