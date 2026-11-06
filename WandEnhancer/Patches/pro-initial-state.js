((state)=>state&&"object"==typeof state&&state.account&&"object"==typeof state.account?{...state,account:{...state.account,subscription:{period:"yearly",state:"active"}}}:state)(${state})
