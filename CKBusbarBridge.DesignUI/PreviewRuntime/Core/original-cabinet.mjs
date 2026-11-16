import originalUi from '../catalog/original-cabinet-ui.json' with {type:'json'};
import {DefinitionError} from './expressions.mjs';
export const originalCabinetUi=originalUi;
export const originalCabinetScenario=scenario=>{const definition=originalUi.scenarios.find(row=>row.key===scenario);if(!definition)throw new DefinitionError('原项目柜体场景不存在');return definition;};
export function createOriginalCabinetInput(scenario){return {version:originalUi.version,values:structuredClone(originalCabinetScenario(scenario).defaults),manual:{},orientation:'CabinetFrontToWall',status:'parameters-only'};}
export function validateOriginalCabinetInputs(records){
  if(records===undefined)return {};
  if(!records||Array.isArray(records)||typeof records!=='object')throw new DefinitionError('柜体参数记录无效');
  for(const [scenario,record]of Object.entries(records)){
    const definition=originalCabinetScenario(scenario);if(record?.version!==originalUi.version||!record.values||typeof record.values!=='object'||!record.manual||typeof record.manual!=='object'||!['CabinetFrontToWall','CabinetRearToWall'].includes(record.orientation)||record.status!=='parameters-only')throw new DefinitionError('柜体参数版本或朝向无效');
    for(const [key,value]of Object.entries(record.values)){const original=definition.defaults[key],unavailableConstraint=scenario==='side-to-side'&&key.endsWith('.Base2.MinimumBaseWidth')&&value===null&&record.values[key.replace('MinimumBaseWidth','HasErrors')]===true;if(!Object.hasOwn(definition.defaults,key)||!unavailableConstraint&&(original!==null&&(typeof original!==typeof value||typeof value==='number'&&!Number.isFinite(value))||original===null&&value!==null&&typeof value!=='string'))throw new DefinitionError('柜体参数类型无效：'+key);}
    for(const key of Object.keys(definition.defaults))if(!Object.hasOwn(record.values,key))throw new DefinitionError('柜体参数缺失：'+key);
    if(Object.values(record.manual).some(value=>typeof value!=='boolean'))throw new DefinitionError('柜体手动输入状态无效');
    if(record.terminalThicknessMm!==undefined&&(!Number.isFinite(record.terminalThicknessMm)||record.terminalThicknessMm<=0))throw new DefinitionError('套管桩头关联板厚无效');
  }
  return structuredClone(records);
}
export function editOriginalCabinetInput(record,scenario,path,value){
  const definition=originalCabinetScenario(scenario),field=definition.fields.find(field=>field.path===path);if(!field||field.readOnly)throw new DefinitionError('字段未对应原项目可输入项');
  const original=definition.defaults[path];if(typeof value!==typeof original&&original!==null||typeof value==='number'&&!Number.isFinite(value)||original===null&&typeof value!=='string')throw new DefinitionError('输入值类型无效');
  if(field.choices.length&&!field.choices.some(choice=>String(choice.value).toLowerCase()===String(value).toLowerCase()))throw new DefinitionError('请选择原项目提供的选项');
  if(path==='General.InsulationDistance'){if(value<=0)throw new DefinitionError('绝缘距离必须为有限正数');if(Math.abs(record.values[path]-value)<.000001)return structuredClone(record);}
  const updated=structuredClone(record),values=updated.values;values[path]=value;updated.manual[path]=true;
  if(scenario==='face-to-face'&&/^[AB]Cabinet\.(FrontHole|BackHole)\.BaseOpeningHeight$/.test(path))values[(path.startsWith('A')?'B':'A')+path.slice(1)]=value;
  if(scenario==='face-to-wall'){
    if(path==='General.CabinetToWallDistance')values['General.CabinetBackToWallDistance']=value;
    if(path==='General.F2')values['General.WallBushingPhaseSpacing']=value;
    const terminal='General.WallBushingTerminal.',specParts=String(values['General.BusbarSpec']).trim().replace(/[×*X]/g,'x').split('x').filter(Boolean),thickness=specParts.length===2?Number(specParts[1]):NaN;
    if(Number.isFinite(thickness)&&thickness>0)updated.terminalThicknessMm=thickness;
    if(path===terminal+'C8'&&!updated.manual[terminal+'C9']&&!updated.manual[terminal+'C10'])values[terminal+'C9']=values[terminal+'C10']=(updated.terminalThicknessMm??8)+value;
    if(path===terminal+'C9'||path===terminal+'C10')values[terminal+'C9']=values[terminal+'C10']=value;
    if(path==='General.BusbarSpec'&&Number.isFinite(thickness)&&thickness>0){if(!updated.manual[terminal+'C8'])values[terminal+'C8']=thickness;if(!updated.manual[terminal+'C9']&&!updated.manual[terminal+'C10'])values[terminal+'C9']=values[terminal+'C10']=thickness+values[terminal+'C8'];}
    for(const hole of ['Cabinet.FrontHole','Cabinet.BackHole']){
      if(path===hole+'.BaseHeight')values[hole+'.IsBaseHeightManuallyModified']=true;
      if(path===hole+'.BaseOpeningHeight')values[hole+'.IsBaseOpeningHeightManuallyModified']=true;
    }
    if(['General.WallHoleEdgeGroundHeight','General.WallHoleHeight','General.CabinetGroundHeight','General.WallInstallation.Top','Cabinet.CabinetHeight'].includes(path))for(const hole of ['Cabinet.FrontHole','Cabinet.BackHole'])if(!values[hole+'.IsBaseHeightManuallyModified']){const height=values['General.WallHoleEdgeGroundHeight']+values['General.WallHoleHeight']-values['General.WallInstallation.Top']-values['Cabinet.CabinetHeight']-values['General.CabinetGroundHeight'];values[hole+'.BaseHeight']=height;values[hole+'.ExtensionBaseHeight']=height>1400?height-1000:0;}
    if(['General.WallHoleHeight','General.WallInstallation.Top','General.WallInstallation.Bottom'].includes(path))for(const hole of ['Cabinet.FrontHole','Cabinet.BackHole'])if(!values[hole+'.IsBaseOpeningHeightManuallyModified']){const height=values['General.WallHoleHeight']-values['General.WallInstallation.Top']-values['General.WallInstallation.Bottom'];if(height>0)values[hole+'.BaseOpeningHeight']=height;}
  }
  if(scenario==='side-to-side'){
    const mode=values['General.ACabinetExposedBusbarPhaseSequence']!==values['General.BCabinetExposedBusbarPhaseSequence']&&values['General.CabinetEccentricity']===0?'LeftRight':'FrontRear';values['General.CabinetTopOpeningMode']=mode;values['General.CabinetTopOpeningModeDisplayName']=mode==='LeftRight'?'左右开孔':'前后开孔';values['General.BackHoleToCabinetEdgeDistanceDisplayName']=mode==='LeftRight'?'距柜前距离：':'距柜后距离：';
    const aliasNames=['ToCabinetEdgeDistance','BusbarCenterToCabinetEdgeDistance','FixedHoleSpacing','L1','L2','L3'],oldMode=record.values['General.CabinetTopOpeningMode'];
    for(const cabinet of ['ACabinet','BCabinet'])for(const side of ['FrontHole','BackHole']){
      const hole=cabinet+'.'+side;
      for(const alias of aliasNames){if(path===hole+'.'+alias)values[hole+'.'+oldMode+'OpeningParameters.'+alias]=value;}
      if(path===cabinet+'.CabinetWidth'&&!updated.manual[hole+'.LeftRightOpeningParameters.FixedHoleSpacing'])values[hole+'.LeftRightOpeningParameters.FixedHoleSpacing']=value-40;
      if(path===hole+'.FixedHoleSpacing')updated.manual[hole+'.'+oldMode+'OpeningParameters.FixedHoleSpacing']=true;
      for(const alias of aliasNames)values[hole+'.'+alias]=values[hole+'.'+mode+'OpeningParameters.'+alias];
      values[hole+'.ActiveBase1Visibility']=mode==='LeftRight'?'Visible':'Collapsed';values[hole+'.ActiveBase2Visibility']=mode==='FrontRear'?'Visible':'Collapsed';
      values[hole+'.Base2.Base2Height']=values[hole+'.Base2.BaseHeight']-values[hole+'.Base2.ElevatedBaseHeight'];
    }
    if(/^[AB]Cabinet\.(FrontHole|BackHole)\.Base[12]\.BaseOpeningHeight$/.test(path))values[(path.startsWith('A')?'B':'A')+path.slice(1)]=value;
    const widths=String(values['General.BusbarSpec']).match(/\d+(?:\.\d+)?/g),barWidth=widths?.length>=2?Number(widths[widths.length>=3?1:0]):NaN;
    const valid=Number.isFinite(barWidth)&&barWidth>0&&values['General.ExposedBusbarPhaseSpacing']>0&&values['General.InsulationDistance']>0;
    const minimum=valid?2*values['General.ExposedBusbarPhaseSpacing']+barWidth+2*(19.5+values['General.InsulationDistance']+5):null;
    for(const cabinet of ['ACabinet','BCabinet'])for(const side of ['FrontHole','BackHole']){const base=cabinet+'.'+side+'.Base2.';values[base+'MinimumBaseWidth']=minimum;values[base+'HasErrors']=!valid||values[base+'BaseWidth']<=0||values[base+'BaseWidth']<minimum||values[base+'InsulatorPosition']<0||values[base+'InsulatorPosition']>values[base+'ElevatedBaseHeight'];}
  }
  validateOriginalCabinetInputs({[scenario]:updated});return updated;
}

// The original face-to-wall window also handles a hidden-to-visible extension
// panel transition in its TextChanged callback, beyond the pure model setter.
export function editOriginalCabinetUiInput(record,scenario,path,value){
  const updated=editOriginalCabinetInput(record,scenario,path,value);
  if(scenario==='face-to-wall'&&/^Cabinet\.(FrontHole|BackHole)\.BaseHeight$/.test(path)&&record.values[path]<=1400&&updated.values[path]>1400){const extension=path.replace(/BaseHeight$/,'ExtensionBaseHeight');updated.values[extension]=updated.values[path]-1000;}
  return updated;
}
