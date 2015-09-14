function interfacingMatlab(prefix, suffix)
concatenatedString = [prefix, ' ' , suffix, '!' ];
fid = fopen('interfacingMatlabOutput.txt' , 'w' );
fprintf(fid, '%s' , concatenatedString);
fclose(fid);